using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BatPlayer.Controls;
using BatPlayer.Audio;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly SettingsService _settings;
    private NowPlayingWindow? _nowPlaying;
    private bool _isClosing;

    private readonly object _geometryGate = new();
    private bool _geometryDirty;
    private System.Windows.Threading.DispatcherTimer? _geometrySaveTimer;

    private readonly bool _restorePlayback;

    public MainWindow(bool restorePlayback = true)
    {
        _restorePlayback = restorePlayback;
        try
        {
            // Resolve services from App container
            var sp = App.Services;
            var library  = sp.GetRequiredService<LibraryService>();
            var audio    = sp.GetRequiredService<AudioService>();
            var settings = sp.GetRequiredService<SettingsService>();
            var tray     = sp.GetRequiredService<TrayService>();
            var hotkeys  = sp.GetRequiredService<GlobalHotkeyService>();
            var search   = sp.GetRequiredService<SearchService>();
            var playlist = sp.GetRequiredService<PlaylistService>();
            var history  = sp.GetRequiredService<HistoryService>();
            var covers   = sp.GetRequiredService<CoverCacheService>();
            var equalizer = sp.GetRequiredService<EqualizerService>();
            var metadata  = sp.GetRequiredService<MetadataService>();
            var soundCloud = sp.GetRequiredService<Services.SoundCloud.SoundCloudService>();
            var soundCloudLogin = sp.GetRequiredService<Services.SoundCloud.SoundCloudLoginService>();
            var soundCloudLikes = sp.GetRequiredService<Database.SoundCloudLikesRepository>();
            var soundCloudCache = sp.GetRequiredService<Services.SoundCloud.SoundCloudStreamCache>();
            var vk = sp.GetRequiredService<Services.Vk.VkService>();
            var vkLogin = sp.GetRequiredService<Services.Vk.VkLoginService>();
            var vkTracks = sp.GetRequiredService<Database.VkTracksRepository>();
            var vkCache = sp.GetRequiredService<Services.Vk.VkStreamCache>();
            var ym = sp.GetRequiredService<Services.YandexMusic.YmService>();
            var ymLogin = sp.GetRequiredService<Services.YandexMusic.YmLoginService>();
            var ymTracks = sp.GetRequiredService<Database.YmTracksRepository>();
            var ymCache = sp.GetRequiredService<Services.YandexMusic.YmStreamCache>();
            var spotify = sp.GetRequiredService<Services.Spotify.SpotifyService>();
            var spotifyTracks = sp.GetRequiredService<Database.SpotifyTracksRepository>();
            var wave = sp.GetRequiredService<Services.RecommendationService>();
            var scApiAuth = sp.GetRequiredService<Services.SoundCloud.SoundCloudOfficialAuth>();

            _vm = new MainViewModel(library, audio, settings, tray, hotkeys, search,
                                    playlist, history, covers, equalizer, metadata,
                                    soundCloud, soundCloudLogin, scApiAuth, soundCloudLikes, soundCloudCache,
                                    vk, vkLogin, vkTracks, vkCache,
                                    ym, ymLogin, ymTracks, ymCache,
                                    spotify, spotifyTracks, wave);
            _hotkeys = hotkeys;
            _settings = settings;

            DataContext = _vm;
            InitializeComponent();
            // The cover viewer closes on a click anywhere in the overlay.
            CoverViewerHost.MouseLeftButtonDown += (_, _) => CloseCoverViewer();
            PreviewKeyDown += (_, e) =>
            {
                if (_coverViewerOpen && e.Key == Key.Escape) CloseCoverViewer();
            };
            Controls.ScrollHeaderCollapse.CollapsedChanged += (_, collapsed) =>
                Dispatcher.BeginInvoke(() => OnPageHeaderCollapsed(collapsed));
            UpdatePageActions();

            ApplyWindowGeometry();

            ContentHost.SizeChanged += (_, _) => ApplyContentClip();
            // Player title + platform icon: text width is the host width minus the icon
            // (16px + 6px margin) — the icon hugs the title; "…" reserves the space.
            PlayerTextHost.SizeChanged += (_, _) =>
                PlayerTitleText.MaxWidth = Math.Max(40, PlayerTextHost.ActualWidth - 24);
            Loaded += (_, _) => ApplyContentClip();

            // When maximized the rounded window corners show through — make them square.
            StateChanged += (_, _) => ApplyCornerRadii();
            SizeChanged += (_, _) => ApplyRootClip();

            _vm.ErrorMessage += (_, msg) => ShowToast(msg);
            _vm.RequestNowPlaying += (_, _) => ToggleNowPlaying();
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Player.PropertyChanged += OnPlayerBarPropertyChanged;
            // Initial sync: if the restored track's cover is already in the VM, fade it
            // into the layer (otherwise the layers stay transparent until the track changes).
            OnBarCoverImageChanged();

            // Stuck hover states after modal dialogs ("Add to playlist" etc.): while the
            // window is disabled WPF doesn't process mouse-leave, so card IsMouseOver stays
            // true and highlights stick. On focus return, re-sync the mouse — WPF recomputes
            // IsMouseOver for all elements.
            Activated += (_, _) => Mouse.PrimaryDevice?.Synchronize();

            KeyDown += OnGlobalKeyDown;
            Closing += OnClosing;
            StateChanged += (_, _) => SaveWindowGeometry();
            LocationChanged += (_, _) => SaveWindowGeometry();
            SizeChanged += (_, _) => SaveWindowGeometry();

            // Defer async init so window shows immediately
            Loaded += async (_, _) =>
            {
                try
                {
                    _hotkeys.Initialize(this);
                    await _vm.InitializeAsync(_restorePlayback);
                    // Restored playback — spin the logo right away.
                    UpdateBatSpin();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "InitializeAsync failed");
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MainWindow constructor failed");
            MessageBox.Show($"{Loc.Get("ErrorWindowInit")}:\n{ex}", Loc.Get("AppName"), MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }


    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {        // Animate on ActivePage, not CurrentPage: Home, Recently played and Favorites
        // are one library VM — CurrentPage didn't change on such transitions, so the
        // animation missed some of them. ActivePage changes exactly once per transition.
        if (e.PropertyName == nameof(MainViewModel.ActivePage))
        {
            AnimatePageChange();
            UpdatePageActions();
        }
    }

    // ==== Play/Pause button logo: spin state ====
    private const double BatSpinSpeedDegPerSec = 400;
    private bool _batSpinning;

    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.CoverImage))
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(OnBarCoverImageChanged);
                return;
            }
            OnBarCoverImageChanged();
            return;
        }

        if (e.PropertyName != nameof(PlayerBarViewModel.IsPlaying)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateBatSpin);
            return;
        }
        UpdateBatSpin();
    }

    // ===== Cover cross-fade in the player bar =====
    // Two layers (BarCoverLayerA/B): the new frame goes into the hidden layer and the
    // layers cross-fade — track changes without an abrupt swap. The layers are transparent
    // when there is no cover — the mouse placeholder shows through.
    private Border? _barCoverFront;
    private BitmapImage? _lastBarCover;
    private int _barCoverGen;

    private void OnBarCoverImageChanged()
    {
        var img = _vm.Player.CoverImage;
        if (img != null)
        {
            _barCoverGen++;
            CrossfadeBarCover(img);
            UpdateTrackTint(img);
            return;
        }

        // LoadCoverAsync first clears the cover and only then loads the new one:
        // don't flash the placeholder between tracks — fade the layers out only if
        // the new cover hasn't arrived within a short window.
        var gen = ++_barCoverGen;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (gen == _barCoverGen && _vm.Player.CoverImage == null)
                {
                    _barCoverFront = null;
                    FadeBarLayer(BarCoverLayerA, 0);
                    FadeBarLayer(BarCoverLayerB, 0);
                }
            });
        });
    }

    private void CrossfadeBarCover(BitmapImage img)
    {
        if (ReferenceEquals(img, _lastBarCover)) return;
        _lastBarCover = img;

        var front = _barCoverFront ?? BarCoverLayerA;
        var back = ReferenceEquals(front, BarCoverLayerA) ? BarCoverLayerB : BarCoverLayerA;
        back.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        FadeBarLayer(back, 1);
        FadeBarLayer(front, 0);
        _barCoverFront = back;
    }

    private static void FadeBarLayer(Border layer, double to)
    {
        // Snapshot BEFORE removing the animation: BeginAnimation(null) reverts opacity
        // to its base value (0) — without pinning the current value the outgoing layer
        // would vanish in one frame and the cross-fade would look like an abrupt swap.
        var current = layer.Opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = current;
        layer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // ===== Page context actions in the window header =====
    // Active page buttons (Shuffle/Sync/Refresh/Import) live in the title bar and switch
    // with it. Show/hide is instant: fade + slide-in from the right read as "lagging" —
    // the curtain was already collapsing while the buttons were still arriving.
    private bool _headerCollapsed;

    private void UpdatePageActions()
    {
        if (PageActionsHost == null || _vm == null) return;

        // The page opens at the top: the curtain is expanded and its actions live in the
        // page header, not in the title bar. On scroll (curtain collapsed) they appear here.
        _headerCollapsed = false;
        RebuildPageActions();
        ApplyActionsVisibility();
    }

    private void RebuildPageActions()
    {
        PageActionsHost.Children.Clear();
        foreach (var b in BuildPageActionButtons(_vm.ActivePage))
            PageActionsHost.Children.Add(b);
    }

    private void ApplyActionsVisibility()
    {
        bool show = _headerCollapsed && PageActionsHost.Children.Count > 0;
        PageActionsHost.IsHitTestVisible = show;
        PageActionsHost.Opacity = show ? 1 : 0;
    }

    private void OnPageHeaderCollapsed(bool collapsed)
    {
        if (_headerCollapsed == collapsed) return;
        _headerCollapsed = collapsed;
        ApplyActionsVisibility();
    }

    private System.Collections.Generic.IEnumerable<Button> BuildPageActionButtons(string page)
    {
        switch (page)
        {
            case "Home":
            case "Library":
            case "Recent":
            case "RecentlyPlayed":
            case "Favorites":
                yield return MakePageAction(_vm.Library.ShuffleTracksCommand, "IconShuffle", "Shuffle");
                break;
            case "Downloads":
                yield return MakePageAction(_vm.Downloads.ShuffleTracksCommand, "IconShuffle", "Shuffle");
                break;
            case "SoundCloud":
                yield return MakePageAction(_vm.SoundCloud.ShuffleCardsCommand, "IconShuffle", "Shuffle");
                yield return MakePageAction(_vm.SoundCloud.SyncNowCommand, "IconSync", "SyncNow");
                break;
            case "YandexMusic":
                yield return MakePageAction(_vm.YmMusic.ShuffleCardsCommand, "IconShuffle", "Shuffle");
                yield return MakeConnectedAction(_vm.YmMusic, _vm.YmMusic.SyncNowCommand, "IconSync", "SyncNow");
                break;
            case "VK":
                yield return MakePageAction(_vm.VkMusic.ShuffleCardsCommand, "IconShuffle", "Shuffle");
                yield return MakeConnectedAction(_vm.VkMusic, _vm.VkMusic.SyncNowCommand, "IconSync", "SyncNow");
                break;
            case "Spotify":
                yield return MakePageAction(_vm.Spotify.ShuffleCardsCommand, "IconShuffle", "Shuffle");
                yield return MakeConnectedAction(_vm.Spotify, _vm.Spotify.SyncNowCommand, "IconSync", "SyncNow");
                yield return MakePageAction(_vm.Spotify.ImportFromFileCommand, "IconAdd", "SpotifyImportButton");
                break;
            case "Wave":
                yield return MakePageAction(_vm.Wave.RefreshCommand, "IconRefresh", "WaveRefresh");
                break;
        }
    }

    private Button MakePageAction(System.Windows.Input.ICommand command, string iconKey, string tooltipKey)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Margin = new Thickness(0, 0, 4, 0),
            Command = command,
            ToolTip = Localization.Loc.Get(tooltipKey)
        };
        b.Content = MakeActionIcon(b, iconKey);
        return b;
    }

    private Button MakeConnectedAction(object vm, System.Windows.Input.ICommand command, string iconKey, string tooltipKey)
    {
        var b = MakePageAction(command, iconKey, tooltipKey);
        b.DataContext = vm; // IsConnected decides whether sync is enabled
        b.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsConnected"));
        return b;
    }

    private System.Windows.Shapes.Path MakeActionIcon(Button owner, string iconKey)
    {
        // 15px matches the icon size of the neighboring title-bar buttons (grid density,
        // files, folder, settings): page buttons don't stand out in size after the move.
        var path = new System.Windows.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Data = (Geometry)FindResource(iconKey)
        };
        path.SetBinding(System.Windows.Shapes.Shape.FillProperty,
            new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        return path;
    }

    private int _tintSeq;
    private bool _tintFrontIsA;

    /// <summary>Window tint: the background is the playing track's cover, blurred
    /// programmatically (a downscale chain — honest averaging, no BlurEffect: WPF's GPU
    /// blur renders at reduced resolution and gives grainy pixels). Dimmed. Track changes
    /// use a smooth two-layer cross-fade.</summary>
    private async void UpdateTrackTint(BitmapSource? img)
    {
        if (TintImgA == null || TintImgB == null) return; // window not ready yet
        var seq = ++_tintSeq;

        var blurred = await Task.Run(() => PreBlurCover(img));
        if (blurred == null || seq != _tintSeq) return; // the track already changed

        var incoming = _tintFrontIsA ? TintImgB : TintImgA;
        var outgoing = _tintFrontIsA ? TintImgA : TintImgB;
        _tintFrontIsA = !_tintFrontIsA;

        incoming.Source = blurred;

        incoming.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(600)));
        outgoing.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
    }

    /// <summary>Programmatic blur: a separable box-blur (3 passes over rows and columns
    /// with a sliding window ≈ Gaussian) over the cover's pixel buffer. Smooth "frosted
    /// glass" without mosaic: a real filter, not resampling. The result is a Frozen
    /// bitmap. Runs on Task.Run.</summary>
    private static BitmapSource? PreBlurCover(BitmapSource? src)
    {
        try
        {
            if (src == null || src.PixelWidth == 0 || src.PixelHeight == 0) return null;
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            var stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);

            // Radius 12 at 256px ≈ blur 60 at 1280 (like the previous BlurEffect 70).
            BoxBlur(px, w, h, 12, 3);

            var bmp = BitmapSource.Create(w, h, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Separable box-blur: passes over rows and columns with a sliding window
    /// (O(1) per pixel). 3 box passes ≈ Gaussian.</summary>
    private static void BoxBlur(byte[] px, int w, int h, int radius, int passes)
    {
        var tmp = new byte[px.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            BoxBlurPass(px, tmp, w, h, radius, horizontal: true);
            BoxBlurPass(tmp, px, w, h, radius, horizontal: false);
        }
    }

    private static void BoxBlurPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        var window = r * 2 + 1;
        if (horizontal)
        {
            for (var y = 0; y < h; y++)
            {
                var rowOff = y * w * 4;
                int rt = 0, gt = 0, bt = 0;
                for (var i = -r; i <= r; i++)
                {
                    var x = Math.Clamp(i, 0, w - 1) * 4 + rowOff;
                    rt += src[x]; gt += src[x + 1]; bt += src[x + 2];
                }
                for (var x = 0; x < w; x++)
                {
                    var o = rowOff + x * 4;
                    dst[o] = (byte)(rt / window);
                    dst[o + 1] = (byte)(gt / window);
                    dst[o + 2] = (byte)(bt / window);
                    dst[o + 3] = 255;
                    var add = Math.Min(x + r + 1, w - 1) * 4 + rowOff;
                    var rem = Math.Max(x - r, 0) * 4 + rowOff;
                    rt += src[add] - src[rem];
                    gt += src[add + 1] - src[rem + 1];
                    bt += src[add + 2] - src[rem + 2];
                }
            }
        }
        else
        {
            var stride = w * 4;
            for (var x = 0; x < w; x++)
            {
                int rt = 0, gt = 0, bt = 0;
                for (var i = -r; i <= r; i++)
                {
                    var y = Math.Clamp(i, 0, h - 1) * stride + x * 4;
                    rt += src[y]; gt += src[y + 1]; bt += src[y + 2];
                }
                for (var y = 0; y < h; y++)
                {
                    var o = y * stride + x * 4;
                    dst[o] = (byte)(rt / window);
                    dst[o + 1] = (byte)(gt / window);
                    dst[o + 2] = (byte)(bt / window);
                    dst[o + 3] = 255;
                    var yAdd = Math.Min(y + r + 1, h - 1) * stride + x * 4;
                    var yRem = Math.Max(y - r, 0) * stride + x * 4;
                    rt += src[yAdd] - src[yRem];
                    gt += src[yAdd + 1] - src[yRem + 1];
                    bt += src[yAdd + 2] - src[yRem + 2];
                }
            }
        }
    }

    // ===== Play/Pause button logo: spinning =====
    // Playing — the logo spins at a constant speed; paused — a short run-out and stop.
    // Spinning is driven by a NATIVE DoubleAnimation (RepeatBehavior=Forever): it has NO
    // per-frame user code. The previous version hooked CompositionTarget.Rendering and set
    // the angle manually, so during cover loading spikes (GC, apply queue) frames were
    // dropped and the spin "micro-froze". The native animation pipeline takes a minimal
    // UI-thread slot.

    // Analytic angle tracking: reading RotateTransform.Angle while an animation is active
    // returned the BASE value (not the animated one) — on pause the logo "jumped back" to
    // the cycle start. The angle is computed from the cycle start time: reliable and frame-free.
    private double _spinBaseAngle;
    private DateTime _spinCycleStartUtc;

    private void UpdateBatSpin()
    {
        if (_vm.Player.IsPlaying)
        {
            if (_batSpinning) return; // already spinning via the native animation — don't restart
            _batSpinning = true;
            // From the current base angle (after the pause run-out the logo isn't at zero).
            // Normalized mod 360: spinning is modular and the angle must not accumulate.
            var from = BatSpinAngle.Angle % 360;
            if (from < 0) from += 360;
            _spinBaseAngle = from;
            _spinCycleStartUtc = DateTime.UtcNow;
            BatSpinAngle.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(360 / BatSpinSpeedDegPerSec))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                });
        }
        else if (_batSpinning)
        {
            _batSpinning = false;
            // Current angle analytically: cycle base + elapsed time × speed.
            var cycleSec = 360.0 / BatSpinSpeedDegPerSec;
            var elapsed = (DateTime.UtcNow - _spinCycleStartUtc).TotalSeconds;
            var current = _spinBaseAngle + (elapsed % cycleSec) / cycleSec * 360;
            BatSpinAngle.BeginAnimation(RotateTransform.AngleProperty, null);
            BatSpinAngle.Angle = current;
            // Run-out inertia: ~170° with smooth decay (like the previous exponential tau=0.45s).
            // NO FillBehavior.Stop: Stop returned the value to base when the run-out finished —
            // the logo visibly BOUNCED back 170° a second after pausing (complaint: "it flips
            // around instead of stopping by inertia"). HoldEnd keeps the final angle; the next
            // start continues from it (the angle is normalized mod 360 at start).
            BatSpinAngle.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(current, current + 170, TimeSpan.FromMilliseconds(1100))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    // ===== Volume: vertical slider in a popup above the button =====
    // The popup opens on hovering the volume button and closes after a short delay once
    // the mouse leaves (time to move from the button to the popup). Slider dragging blocks
    // closing: mouse capture holds the mouse even outside the popup, so closing is
    // deferred to LostMouseCapture.

    private System.Windows.Threading.DispatcherTimer? _volumeCloseTimer;

    // Volume panel open (an overlay inside the bar, not a Popup — that one opened
    // "detached from the window" on target failure/window drag).
    private bool _volumePanelOpen;

    // A close animation is in progress: hovering now cancels the close — otherwise the
    // panel played its fade to zero and "didn't pop up" on the next hover.
    private bool _volumePanelClosing;

    private void VolumeButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumeCloseTimer?.Stop();
        CancelVolumePanelClosing();
        ShowVolumePanel();
    }

    private void VolumeButton_MouseLeave(object sender, MouseEventArgs e) => ScheduleVolumeClose();

    private void VolumePopup_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumeCloseTimer?.Stop();
        CancelVolumePanelClosing();
    }

    private void VolumePopup_MouseLeave(object sender, MouseEventArgs e) => ScheduleVolumeClose();

    private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e) => ScheduleVolumeClose();

    /// <summary>Panel opening: floats up — a slight rise + fade.</summary>
    private void ShowVolumePanel()
    {
        _volumePanelOpen = true;
        VolumePopupPanel.Visibility = Visibility.Visible;
        VolumePopupPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        VolumePopupSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ScheduleVolumeClose()
    {
        // 450ms — time to move from the button to the panel across the gap; while the thumb
        // is dragged the panel stays open (the thumb holds mouse capture) and closes after
        // release via LostMouseCapture.
        _volumeCloseTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _volumeCloseTimer.Tick -= VolumeCloseTimer_Tick;
        _volumeCloseTimer.Tick += VolumeCloseTimer_Tick;
        _volumeCloseTimer.Start();
    }

    private void VolumeCloseTimer_Tick(object? sender, EventArgs e)
    {
        _volumeCloseTimer?.Stop();
        // The mouse returned to the button/panel or a drag is ongoing — stay open;
        // while dragging, closing comes via LostMouseCapture.
        if (VolumeSlider.IsMouseCaptureWithin || VolumePopupPanel.IsMouseOver || VolumeButton.IsMouseOver)
            return;
        CloseVolumePanelAnimated();
    }

    /// <summary>Panel close with a short fade-out and downward slide: without it the panel
    /// vanished instantly, which next to the "float in" on open looked like a cut-off.</summary>
    private void CloseVolumePanelAnimated()
    {
        if (!_volumePanelOpen || _volumePanelClosing) return;
        _volumePanelClosing = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
        var slide = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };

        VolumePopupPanel.BeginAnimation(UIElement.OpacityProperty, fade);
        VolumePopupSlide.BeginAnimation(TranslateTransform.YProperty, slide);

        fade.Completed += (_, _) =>
        {
            _volumePanelClosing = false;
            _volumePanelOpen = false;
            // The mouse made it back during the fade-out — keep the panel open.
            if (VolumeSlider.IsMouseCaptureWithin || VolumePopupPanel.IsMouseOver || VolumeButton.IsMouseOver)
            {
                ResetVolumePanelVisual();
                VolumePopupPanel.Visibility = Visibility.Visible;
                return;
            }
            ResetVolumePanelVisual();
            VolumePopupPanel.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>Cancel a running close (mouse returned): the animation is silenced and the
    /// panel instantly returns to the open state.</summary>
    private void CancelVolumePanelClosing()
    {
        if (!_volumePanelClosing) return;
        _volumePanelClosing = false;
        ResetVolumePanelVisual();
        VolumePopupPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Reset the panel's opacity/offset after closing: the next opening animates
    /// them from the original values (0 → 1, 10 → 0).</summary>
    private void ResetVolumePanelVisual()
    {
        VolumePopupPanel.BeginAnimation(UIElement.OpacityProperty, null);
        VolumePopupPanel.Opacity = 1;
        VolumePopupSlide.BeginAnimation(TranslateTransform.YProperty, null);
        VolumePopupSlide.Y = 0;
    }

    // ===== Cover viewer: click on the player bar cover =====
    // Overlay inside the window: the background is the same cover, stretched and blurred
    // (BlurEffect) under a dimmer; the cover itself is large and undistorted. A click
    // anywhere (including a second click on the avatar) closes it — no close button.
    // The full size is decoded asynchronously (the bar's 256px version would blur).
    private bool _coverViewerOpen;

    private int _coverViewerSeq;

    private void BarCover_Click(object sender, MouseButtonEventArgs e)
    {
        // The avatar is a toggle: an open viewer is closed by the same click.
        if (_coverViewerOpen)
        {
            CloseCoverViewer();
            return;
        }

        var img = _vm.Player.CoverImage;
        if (img == null) return;
        _coverViewerOpen = true;
        var seq = ++_coverViewerSeq;

        CoverViewerHost.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CoverViewerBlur.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        CoverViewerImage.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        var scale = new ScaleTransform(0.94, 0.94);
        CoverViewerImage.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

        ApplyCoverViewerImage(img);

        // Full-size decode in the background: replaces the mini version (256px) when ready.
        var path = _vm.Player.CurrentTrack?.CoverCachePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 1200;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    Dispatcher.Invoke(() =>
                    {
                        if (seq == _coverViewerSeq) ApplyCoverViewerImage(bmp);
                    });
                }
                catch { /* keep the mini version */ }
            });
        }
    }

    private void ApplyCoverViewerImage(BitmapSource bmp)
    {
        CoverViewerImage.Source = bmp;
    }

    private void CloseCoverViewer()
    {
        if (!_coverViewerOpen) return;
        _coverViewerOpen = false;
        _coverViewerSeq++;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (_coverViewerOpen) return;
            CoverViewerHost.Visibility = Visibility.Collapsed;
            CoverViewerImage.Source = null;
        };
        CoverViewerBlur.BeginAnimation(UIElement.OpacityProperty, fade);
        CoverViewerImage.BeginAnimation(UIElement.OpacityProperty, fade);
    }


    /// <summary>"+ add to playlist" from the player: adds the currently playing track.</summary>
    private void AddToPlaylistFromPlayer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm.Player.CurrentTrack is Models.Track track)
            Views.PlaylistDialogs.AddTrackToPlaylist(track);
    }

    private void ApplyContentClip()
    {
        // The inner UI is rounded on all sides; beneath the clip is the theme backing
        // (RootBorder), not the desktop. Corners are square when maximized.
        var r = WindowState == WindowState.Maximized ? 0 : 12;
        ContentHost.Clip = new RectangleGeometry(
            new Rect(0, 0, ContentHost.ActualWidth, ContentHost.ActualHeight), r, r);
    }

    private void ApplyCornerRadii()
    {
        var r = WindowState == WindowState.Maximized ? 0 : 12;
        RootBorder.CornerRadius = new CornerRadius(r);
        ApplyContentClip();
        ApplyRootClip();
    }

    /// <summary>
    /// All content (including the GIF/video background) is clipped to the window rounding:
    /// background corners don't stick out past the frame, and beneath the inner rounding is
    /// the app backing (RootBorder), not the Windows desktop.
    /// </summary>
    private void ApplyRootClip()
    {
        RootGrid.Clip = new RectangleGeometry(
            new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight),
            WindowState == WindowState.Maximized ? 0 : 12,
            WindowState == WindowState.Maximized ? 0 : 12);
    }

    /// <summary>Page-change transition: a short fade without movement.</summary>
    private void AnimatePageChange()
    {
        if (!_settings.Current.AnimationsEnabled) return;

        // Only a short fade, no slide: the previous 32px horizontal shift "moved" the
        // card area and read as a content jump.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        PageHost.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void ApplyWindowGeometry()
    {
        var s = _settings.Current;
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowGeometry()
    {
        if (_isClosing) return;
        // Debounce: DragMove/resize fire SizeChanged/LocationChanged dozens of times per
        // second, and each change would serialize settings.json to disk. Accumulate the
        // changes and write at most once every half second; the final save is in OnClosing.
        lock (_geometryGate)
        {
            _geometryDirty = true;
        }
        _geometrySaveTimer?.Stop();
        _geometrySaveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _geometrySaveTimer.Tick -= GeometrySaveTimer_Tick;
        _geometrySaveTimer.Tick += GeometrySaveTimer_Tick;
        _geometrySaveTimer.Start();
    }

    private void GeometrySaveTimer_Tick(object? sender, EventArgs e)
    {
        _geometrySaveTimer?.Stop();
        bool dirty;
        lock (_geometryGate)
        {
            dirty = _geometryDirty;
            _geometryDirty = false;
        }
        if (!dirty || _isClosing) return;
        _settings.Update(s =>
        {
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            s.WindowMaximized = WindowState == WindowState.Maximized;
        });
    }

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        // In-app hotkeys (not global OS-level)
        var modifiers = (Keyboard.Modifiers == ModifierKeys.Control ? 2 : 0)
                      | (Keyboard.Modifiers == ModifierKeys.Alt     ? 1 : 0)
                      | (Keyboard.Modifiers == ModifierKeys.Shift   ? 4 : 0);

        var s = _settings.Current;
        var key = (int)KeyInterop.VirtualKeyFromKey(e.Key);

        if (MatchKey(s.PlayPause, key, modifiers)) { _vm.Player.PlayPauseCommand.Execute(null); e.Handled = true; }
        else if (MatchKey(s.NextTrack, key, modifiers)) { _vm.Player.NextCommand.Execute(null); e.Handled = true; }
        else if (MatchKey(s.PreviousTrack, key, modifiers)) { _vm.Player.PreviousCommand.Execute(null); e.Handled = true; }
        else if (MatchKey(s.VolumeUp, key, modifiers)) { _vm.Player.Volume = Math.Min(100, _vm.Player.Volume + 5); e.Handled = true; }
        else if (MatchKey(s.VolumeDown, key, modifiers)) { _vm.Player.Volume = Math.Max(0, _vm.Player.Volume - 5); e.Handled = true; }
        else if (MatchKey(s.Mute, key, modifiers)) { _vm.Player.ToggleMuteCommand.Execute(null); e.Handled = true; }
        else if (MatchKey(s.Favorite, key, modifiers)) { _vm.Player.ToggleFavoriteCommand.Execute(null); e.Handled = true; }
        else if (MatchKey(s.Search, key, modifiers)) { SearchBox.Focus(); e.Handled = true; }
    }

    private static bool MatchKey(Models.HotkeyBinding b, int key, int mods) => b.Key == key && b.Modifiers == mods;

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            DragMove();
        }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Current.CloseToTray)
        {
            Hide();
        }
        else Close();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Clearing the box resets the active page's filter (SearchText routes to
            // MainViewModel); focus is moved out of the box.
            SearchBox.Clear();
            Keyboard.ClearFocus();
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            _ = _vm.HandleDropAsync(paths);
        }
    }

    private void ToggleNowPlaying()
    {
        // Single expanded now-playing window; the expand button toggles it.
        if (_nowPlaying != null)
        {
            _nowPlaying.Close();
            return;
        }

        var np = new NowPlayingWindow { Owner = this };
        np.Closed += (_, _) =>
        {
            _nowPlaying = null;
            _vm.IsNowPlayingOpen = false;
        };
        np.SetContext(_vm.Player);
        _nowPlaying = np;
        _vm.IsNowPlayingOpen = true;
        np.Show();
    }

    private void ShowToast(string msg)
    {
        // Simple in-app status notification
        Dispatcher.Invoke(() =>
        {
            // For now, just log; could be expanded into a toast control
            Logger.Warn($"User-facing error: {msg}");
        });
    }

    /// <summary>Forced close on window recreation (bypassing "close to tray").</summary>
    public void DestroyForRecreate()
    {
        _hotkeys.UnregisterAll();
        _isClosing = true;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // The geometry timer is still running: apply the accumulated changes before saving.
        _geometrySaveTimer?.Stop();
        GeometrySaveTimer_Tick(null, EventArgs.Empty);

        // Window recreation: close for real, bypassing "close to tray".
        if (_isClosing)
        {
            await App.Services.GetRequiredService<AudioService>().SaveStateAsync();
            await App.Services.GetRequiredService<SettingsService>().SaveAsync();
            return;
        }
        // X button and taskbar "Close window" hide to tray; the process only
        // exits via the tray menu "Exit" (which sets App.IsExiting first).
        if (_settings.Current.CloseToTray && !App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_isClosing) return;
        _isClosing = true;

        await App.Services.GetRequiredService<AudioService>().SaveStateAsync();
        await App.Services.GetRequiredService<SettingsService>().SaveAsync();
    }
}
