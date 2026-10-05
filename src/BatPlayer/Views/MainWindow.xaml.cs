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
            // Просмотрщик обложки закрывается кликом в любом месте оверлея.
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
            // Название плеера + значок платформы: текст занимает ширину хоста минус
            // значок (16px + 6px отступ) — значок прижат к названию, «…» резервирует место.
            PlayerTextHost.SizeChanged += (_, _) =>
                PlayerTitleText.MaxWidth = Math.Max(40, PlayerTextHost.ActualWidth - 24);
            Loaded += (_, _) => ApplyContentClip();

            // В развёрнутом виде скруглённые углы окна просвечивают — делаем их прямыми.
            StateChanged += (_, _) => ApplyCornerRadii();
            SizeChanged += (_, _) => ApplyRootClip();

            _vm.ErrorMessage += (_, msg) => ShowToast(msg);
            _vm.RequestNowPlaying += (_, _) => ToggleNowPlaying();
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Player.PropertyChanged += OnPlayerBarPropertyChanged;
            // Стартовая синхронизация: если обложка восстановленного трека уже в VM,
            // проявляем её в слое (иначе слои останутся прозрачными до смены трека).
            OnBarCoverImageChanged();

            // Залипшие hover-состояния после модальных диалогов (Add to playlist и т.п.):
            // пока окно disabled, WPF не обрабатывает уход мыши, и IsMouseOver карточек
            // остаётся true — подложки «залипают». При возврате фокуса ресинхронизируем
            // мышь — WPF пересчитает IsMouseOver у всех элементов.
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
                    // Восстановленное воспроизведение — раскручиваем логотип сразу.
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
    {        // Анимируем по ActivePage, а не CurrentPage: «Главная», «Недавно
        // прослушанные», «Любимые» — одна VM библиотеки, CurrentPage при таких
        // переходах не менялся, и анимация срабатывала не на всех переходах.
        // ActivePage меняется ровно один раз на каждый переход.
        if (e.PropertyName == nameof(MainViewModel.ActivePage))
        {
            AnimatePageChange();
            UpdatePageActions();
        }
    }

    // ==== Логотип на кнопке Play/Pause: состояние вращения ====
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

    // ===== Кроссфейд обложки в плеер-баре =====
    // Два слоя (BarCoverLayerA/B): новый кадр кладётся в скрытый слой, слои
    // меняются прозрачностью навстречу — смена трека без резкой подмены.
    // Слои прозрачны и когда обложки нет — сквозь них видна мышь-плейсхолдер.
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

        // LoadCoverAsync сперва сбрасывает обложку и лишь затем грузит новую:
        // не мигаем плейсхолдером между треками — растворяем слои, только если
        // новая обложка не приехала в течение короткого окна.
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
        // Snapshot ДО снятия анимации: BeginAnimation(null) откатывает прозрачность
        // к базовой (0) — без фиксации текущего значения уходящий слой исчезал бы
        // одним кадром, и кроссфейд выглядел как резкая подмена.
        var current = layer.Opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = current;
        layer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // ===== Контекстные действия страницы в шапке окна =====
    // Кнопки активной страницы (Shuffle/Sync/Refresh/Import) живут в тайтл-баре и
    // сменяются вместе с ней. Появление/скрытие мгновенные: fade + выезд справа
    // читались как «провисание» — шторка уже сворачивается, а кнопки ещё доехали не были.
    private bool _headerCollapsed;

    private void UpdatePageActions()
    {
        if (PageActionsHost == null || _vm == null) return;

        // Страница открывается сверху: шторка развёрнута, действия живут в её шапке,
        // в тайтл-баре их нет. При скролле (шторка свернулась) они появляются здесь.
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
        b.DataContext = vm; // IsConnected решает, активна ли синхронизация
        b.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsConnected"));
        return b;
    }

    private System.Windows.Shapes.Path MakeActionIcon(Button owner, string iconKey)
    {
        // 15px — размер иконок соседних кнопок тайтл-бара (плотность сетки, файлы,
        // папка, настройки): кнопки страниц не выделяются габаритом после переезда.
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
    private Color[]? _lastTintColors;
    private System.Windows.Threading.DispatcherTimer? _tintRefreshDelay;
    private bool _tintLifeStarted;

    /// <summary>Окрас окна: фон — обложка играющего трека, размытая программно
    /// (цепочка даунскейлов — честное усреднение, без BlurEffect: GPU-блюр WPF
    /// рендерится в пониженном разрешении и даёт зернистые пиксели). Затемнённая.
    /// Смена трека — плавный кроссфейд двух слоёв.</summary>
    private async void UpdateTrackTint(BitmapSource? img)
    {
        if (TintImgA == null || TintImgB == null) return; // окно ещё не готово
        var seq = ++_tintSeq;

        var blurred = await Task.Run(() => PreBlurCover(img));
        if (blurred == null || seq != _tintSeq) return; // трек уже сменился

        var incoming = _tintFrontIsA ? TintImgB : TintImgA;
        var outgoing = _tintFrontIsA ? TintImgA : TintImgB;
        _tintFrontIsA = !_tintFrontIsA;

        incoming.Source = blurred;

        incoming.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(600)));
        outgoing.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
    }

    /// <summary>Программное размытие: сепарабельный box-blur (3 прохода по строкам
    /// и столбцам со скользящим окном ≈ гауссиана) по пиксельному буферу обложки.
    /// Гладкое «матовое стекло» без мозаики: настоящий фильтр, а не ресемплинг.
    /// Результат — Frozen bitmap. Фон: Task.Run.</summary>
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

            // Радиус 12 при 256px ≈ блюр 60 при 1280 (как прежний BlurEffect 70).
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

    /// <summary>Сепарабельный box-blur: passes проходов по строкам и столбцам со
    /// скользящим окном (O(1) на пиксель). 3 прохода box ≈ гауссиана.</summary>
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

    // ===== Логотип на кнопке Play/Pause: вращение =====
    // Играет — логотип крутится с постоянной скоростью; пауза — короткий выбег и стоп.
    // Вращение ведёт ШТАТНАЯ DoubleAnimation (RepeatBehavior=Forever): у неё НЕТ
    // пользовательского кода на каждый кадр. Прежний вариант висел на
    // CompositionTarget.Rendering и выставлял угол руками, из-за чего в моменты
    // прогрузки обложек (GC, очередь применов) кадры пропускались и вращение
    // «микрофризило». Нативный анимационный конвейер ест минимальный слот UI-потока.

    // Аналитический трекинг угла: чтение RotateTransform.Angle при активной анимации
    // возвращало БАЗОВОЕ значение (не анимированное) — при паузе лого «откидывало назад»
    // к старту цикла. Угол считается по времени старта цикла: надёжно и без кадров.
    private double _spinBaseAngle;
    private DateTime _spinCycleStartUtc;

    private void UpdateBatSpin()
    {
        if (_vm.Player.IsPlaying)
        {
            if (_batSpinning) return; // уже крутится нативной анимацией — не перезапускать
            _batSpinning = true;
            // От текущего базового угла (после выбега паузы логотип замер не на нуле).
            // Нормализация по модулю 360: вращение модульно, а копиться угол не должен.
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
            // Текущий угол — аналитически: база цикла + прошедшее время × скорость.
            var cycleSec = 360.0 / BatSpinSpeedDegPerSec;
            var elapsed = (DateTime.UtcNow - _spinCycleStartUtc).TotalSeconds;
            var current = _spinBaseAngle + (elapsed % cycleSec) / cycleSec * 360;
            BatSpinAngle.BeginAnimation(RotateTransform.AngleProperty, null);
            BatSpinAngle.Angle = current;
            // Инерция выбега: ~170° с плавным затуханием (как прежняя экспонента tau=0.45с).
            // БЕЗ FillBehavior.Stop: Stop по завершении выбега возвращал значение к базе —
            // лого визуально ОТСКАКИВАЛО назад на 170° через секунду после паузы
            // (жалоба: «разворачивает, а не по инерции перестаёт двигаться»).
            // HoldEnd держит конечный угол; следующий запуск продолжит с него
            // (угол нормализуется по модулю 360 при старте).
            BatSpinAngle.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(current, current + 170, TimeSpan.FromMilliseconds(1100))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    // ===== Громкость: вертикальный слайдер в popup над кнопкой =====
    // Popup открывается наведением на кнопку громкости и закрывается с короткой
    // задержкой после ухода мыши (время переехать с кнопки на popup). Перетаскивание
    // ползунка закрытие блокирует: mouse capture держит мышь даже за границами popup,
    // закрытие откладывается до LostMouseCapture.

    private System.Windows.Threading.DispatcherTimer? _volumeCloseTimer;

    // Панель громкости открыта (оверлей внутри бара, не Popup — тот открывался
    // «отдельно от окна» при сбое таргета/перетаскивании окна).
    private bool _volumePanelOpen;

    // Идёт анимация закрытия: наведение в этот момент отменяет закрытие —
    // иначе панель доигрывала fade до нуля и «не всплывала» при повторном наведении.
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

    /// <summary>Открытие панели: «выплывание» вверх — лёгкий подъём + fade.</summary>
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
        // 450 мс — время переехать с кнопки на панель через зазор; при драге бегунка
        // панель держится открытой (thumb держит mouse capture), закрывается после
        // отпускания по LostMouseCapture.
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
        // Мышь вернулась на кнопку/панель или drag продолжается — остаёмся открытыми;
        // при drag-е закрытие придёт по LostMouseCapture.
        if (VolumeSlider.IsMouseCaptureWithin || VolumePopupPanel.IsMouseOver || VolumeButton.IsMouseOver)
            return;
        CloseVolumePanelAnimated();
    }

    /// <summary>Закрытие панели с коротким fade-out и сползанием вниз: без него панель
    /// исчезала мгновенно, что рядом с «выплыванием» при открытии выглядело обрывом.</summary>
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
            // За время fade-out мышь успела вернуться — оставляем открытой.
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

    /// <summary>Отмена идущего закрытия (мышь вернулась): анимация гасится, панель
    /// мгновенно возвращается в открытое состояние.</summary>
    private void CancelVolumePanelClosing()
    {
        if (!_volumePanelClosing) return;
        _volumePanelClosing = false;
        ResetVolumePanelVisual();
        VolumePopupPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Сброс прозрачности/сдвига панели после закрытия: следующее открытие
    /// анимирует их от исходных значений (0 → 1, 10 → 0).</summary>
    private void ResetVolumePanelVisual()
    {
        VolumePopupPanel.BeginAnimation(UIElement.OpacityProperty, null);
        VolumePopupPanel.Opacity = 1;
        VolumePopupSlide.BeginAnimation(TranslateTransform.YProperty, null);
        VolumePopupSlide.Y = 0;
    }

    // ===== Просмотрщик обложки: клик по обложке в плеер-баре =====
    // Оверлей внутри окна: фон — та же обложка, растянутая и размытая (BlurEffect)
    // под затемнением; сама обложка — крупно, без искажений. Клик в любом месте
    // (включая повторный клик по аватарке) закрывает — крестика больше нет.
    // Полный размер декодируется асинхронно (256px версия бара мылится).
    private bool _coverViewerOpen;

    private int _coverViewerSeq;

    private void BarCover_Click(object sender, MouseButtonEventArgs e)
    {
        // Аватарка — тоггл: открытый просмотрщик закрывается тем же кликом.
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

        // Полноразмерный декод в фоне: мини-версия (256px) замещается, когда готова.
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
                catch { /* остаётся мини-версия */ }
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


    /// <summary>Скругляет углы внутренней области контента, как в оригинальном приложении.</summary>
    /// <summary>«+ в плейлист» из плеера: добавляет играющий сейчас трек.</summary>
    private void AddToPlaylistFromPlayer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm.Player.CurrentTrack is Models.Track track)
            Views.PlaylistDialogs.AddTrackToPlaylist(track);
    }

    /// <summary>
    /// Бегущая строка: если текст не влезает в свою колонку — плавно прокатываем
    /// его туда-обратно (TranslateTransform), влезает — анимация снимается.
    /// </summary>

    /// <summary>Элемент ограничен родителем (ActualWidth), контент — DesiredSize:
    /// разница и есть вылезание; катаем туда-обратно, влезает — без анимации.</summary>

    private void ApplyContentClip()
    {
        // Внутренний интерфейс скруглён со всех сторон; под срезом — подложка темы
        // (RootBorder), а не рабочий стол. В развёрнутом виде углы прямые.
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
    /// Весь контент (включая GIF/видео-фон) обрезается по скруглению окна: уголки
    /// фона не вылезают за рамку, а под внутренними скруглениями остаётся подложка
    /// приложения (RootBorder), а не рабочий стол Windows.
    /// </summary>
    private void ApplyRootClip()
    {
        RootGrid.Clip = new RectangleGeometry(
            new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight),
            WindowState == WindowState.Maximized ? 0 : 12,
            WindowState == WindowState.Maximized ? 0 : 12);
    }

    /// <summary>Переход при смене страницы: короткий фейд без перемещения.</summary>
    private void AnimatePageChange()
    {
        if (!_settings.Current.AnimationsEnabled) return;

        // Только короткий фейд, без слайда: прежний горизонтальный сдвиг 32px
        // «смещал» область с карточками и читался как прыжок контента.
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
        // Дебаунс: DragMove/resize шлют SizeChanged/LocationChanged десятки раз в секунду,
        // и каждая мутация гоняла бы сериализацию settings.json на диск. Копим изменения,
        // пишем не чаще раза в полсекунды; финальный сохранение — в OnClosing.
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
            // Очистка строки снимает фильтр активной страницы (SearchText маршрутизируется
            // в MainViewModel); фокус из поля убираем.
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

    /// <summary>Принудительное закрытие при пересоздании окна (минуя «свернуть в трей»).</summary>
    public void DestroyForRecreate()
    {
        _hotkeys.UnregisterAll();
        _isClosing = true;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Неотписанный таймер геометрии: применяем накопленные изменения до сохранения.
        _geometrySaveTimer?.Stop();
        GeometrySaveTimer_Tick(null, EventArgs.Empty);

        // Пересоздание окна: закрываемся по-настоящему, минуя «свернуть в трей».
        if (_isClosing)
        {
            await App.Services.GetRequiredService<AudioService>().SaveStateAsync();
            await App.Services.GetRequiredService<SettingsService>().SaveAsync();
            return;
        }
        // X button and taskbar "Close window" hide to tray; the process only
        // exits via tray menu "Выход" (which sets App.IsExiting first).
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
