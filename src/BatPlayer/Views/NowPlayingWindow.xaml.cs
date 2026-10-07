using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class NowPlayingWindow : Window
{
    private PlayerBarViewModel? _player;
    private Image? _nowCoverFront;
    private ImageSource? _lastNowCover;

    public NowPlayingWindow()
    {
        InitializeComponent();
        // Square corners when maximized so the corner radius doesn't show.
        StateChanged += (_, _) =>
            RootBorder.CornerRadius = new CornerRadius(
                WindowState == WindowState.Maximized ? 0 : 12);
    }

    public void SetContext(PlayerBarViewModel player)
    {
        if (!ReferenceEquals(_player, player))
        {
            if (_player != null) _player.PropertyChanged -= OnPlayerPropertyChanged;
            _player = player;
            _player.PropertyChanged += OnPlayerPropertyChanged;
            _lastNowCover = null;
            _nowCoverFront = null;
        }
        DataContext = player;
        // Initial sync: if the cover is already in the VM, fade it in
        // (otherwise the layers stay transparent with the mouse overlay on top).
        OnNowCoverChanged();
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.CoverImage))
            Dispatcher.BeginInvoke(OnNowCoverChanged);
    }

    // ===== Large cover cross-fade =====
    private void OnNowCoverChanged()
    {
        var img = _player?.CoverImage;
        if (img == null)
        {
            // No cover: fade the layers out so the bat border shows through.
            _nowCoverFront = null;
            FadeLayer(NowCoverLayerA, 0);
            FadeLayer(NowCoverLayerB, 0);
            FadeLayer(NowBatBorder, 1);
            return;
        }

        if (ReferenceEquals(img, _lastNowCover)) return;
        _lastNowCover = img;

        var front = _nowCoverFront ?? NowCoverLayerA;
        var back = ReferenceEquals(front, NowCoverLayerA) ? NowCoverLayerB : NowCoverLayerA;
        back.Source = img;
        FadeLayer(back, 1);
        FadeLayer(front, 0);
        FadeLayer(NowBatBorder, 0);
        _nowCoverFront = back;
    }

    private static void FadeLayer(UIElement layer, double to)
    {
        // Snapshot BEFORE removing the animation: BeginAnimation(null) reverts opacity
        // to its base value — without pinning the current value the outgoing layer
        // would vanish in one frame and the cross-fade would look like an abrupt swap.
        if (layer is FrameworkElement fe)
        {
            var current = fe.Opacity;
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            fe.Opacity = current;
        }
        layer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
