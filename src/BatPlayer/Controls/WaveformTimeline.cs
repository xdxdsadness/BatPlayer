using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BatPlayer.Services;

namespace BatPlayer.Controls;

/// <summary>
/// SoundCloud-style timeline: the track's waveform (real amplitude peaks), the played
/// part highlighted with the accent, the rest dimmed, the position — a vertical line.
/// A click anywhere seeks; holding the button — the playhead follows the cursor, with
/// the final seek on release. Peaks are computed in the background (WaveformCache)
/// and cached for the session.
///
/// Smoothness: the bars' geometry is built ONCE per (peaks, size) pair and frozen — a
/// position tick redraws two DrawGeometry calls + the playhead (a cheap frame), and
/// between position-timer ticks the playhead is interpolated on every render frame
/// (CompositionTarget.Rendering), so the line moves continuously instead of stepping
/// 4 times a second.
/// </summary>
public sealed class WaveformTimeline : FrameworkElement
{
    public static readonly DependencyProperty PeaksSourceProperty =
        DependencyProperty.Register(nameof(PeaksSource), typeof(string), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(string.Empty, OnPeaksSourceChanged));

    public static readonly DependencyProperty PositionSecondsProperty =
        DependencyProperty.Register(nameof(PositionSeconds), typeof(double), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationSecondsProperty =
        DependencyProperty.Register(nameof(DurationSeconds), typeof(double), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeekCommandProperty =
        DependencyProperty.Register(nameof(SeekCommand), typeof(ICommand), typeof(WaveformTimeline),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PlayedBrushProperty =
        DependencyProperty.Register(nameof(PlayedBrush), typeof(Brush), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnplayedBrushProperty =
        DependencyProperty.Register(nameof(UnplayedBrush), typeof(Brush), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBrushProperty =
        DependencyProperty.Register(nameof(PlayheadBrush), typeof(Brush), typeof(WaveformTimeline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public string PeaksSource { get => (string)GetValue(PeaksSourceProperty); set => SetValue(PeaksSourceProperty, value); }
    public double PositionSeconds { get => (double)GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }
    public double DurationSeconds { get => (double)GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public Brush? PlayedBrush { get => (Brush?)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush? UnplayedBrush { get => (Brush?)GetValue(UnplayedBrushProperty); set => SetValue(UnplayedBrushProperty, value); }
    public Brush? PlayheadBrush { get => (Brush?)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    private const double BarWidth = 2.0;
    private const double BarStep = 3.0;
    private const double PlayheadWidth = 2.0;

    /// <summary>Max interpolation distance after a tick: beyond it the track is paused
    /// or a tick was lost; the playhead freezes at the last position.</summary>
    private const double MaxInterpolationSeconds = 0.45;

    private float[] _peaks = Array.Empty<float>();
    private CancellationTokenSource? _loadCts;
    private bool _dragging;
    private double _dragFrac;
    private long _lastDragSeekTick;

    // Bars geometry cache: rebuilt only when the peaks/size change.
    private Geometry? _barsGeometry;
    private float[]? _geoPeaks;
    private double _geoW = -1, _geoH = -1;

    // Position interpolation between timer ticks.
    private DateTime _lastTickUtc = DateTime.MinValue;
    private double _lastTickPosition;
    // The width fraction where the playhead was last drawn: OnFrame triggers a redraw
    // only when the playhead actually moved by at least a pixel. Previously the
    // invalidate ran EVERY render frame while the track played — a full OnRender
    // (two bars-geometry draws + clip) 60 times a second.
    private double _drawnPlayheadFrac = -1;

    public WaveformTimeline()
    {
        SizeChanged += (_, _) => { _barsGeometry = null; _drawnPlayheadFrac = -1; InvalidateVisual(); };
        // The per-frame render subscription lives only on a LOADED control: the
        // constructor subscription never unhooked, and every NowPlaying window
        // open/close left a dead OnFrame ticking in the render loop forever
        // (and invalidating the detached tree while it still had peaks).
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame; // guard against double Loaded
            CompositionTarget.Rendering += OnFrame;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private static void OnPeaksSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (WaveformTimeline)d;
        c._loadCts?.Cancel();
        var path = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            c._peaks = Array.Empty<float>();
            c._barsGeometry = null;
            c.InvalidateVisual();
            return;
        }

        var cts = c._loadCts = new CancellationTokenSource();
        var token = cts.Token;
        var ctl = c;
        Task.Run(async () =>
        {
            var peaks = await WaveformCache.GetPeaksAsync(path);
            if (token.IsCancellationRequested) return;
            ctl._peaks = peaks;
            ctl._barsGeometry = null;
            try { ctl.Dispatcher.Invoke(ctl.InvalidateVisual); } catch { /* window closed */ }
        }, CancellationToken.None);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // A moving frame is only needed while the interpolation is alive (playing) and there's something to draw.
        if (_peaks.Length == 0 || DurationSeconds <= 0.5) return;
        if ((DateTime.UtcNow - _lastTickUtc).TotalMilliseconds > MaxInterpolationSeconds * 1000) return;

        // Pixel threshold: redraw only if the playhead moved at least 0.75 px since the
        // last draw (at 60 fps the inter-frame step is usually < 0.5 px — most frames
        // are skipped with no visible loss).
        var frac = DurationSeconds > 0 ? DisplayPosition / DurationSeconds : 0;
        if (_drawnPlayheadFrac >= 0 &&
            Math.Abs(frac - _drawnPlayheadFrac) * ActualWidth < 0.75) return;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PositionSecondsProperty)
        {
            _lastTickPosition = (double)e.NewValue;
            _lastTickUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Position with inter-tick interpolation: the tick arrives 10 times a second;
    /// in between the position linearly catches up (no further than MaxInterpolationSeconds —
    /// on pause the interpolation dies out).</summary>
    private double DisplayPosition
    {
        get
        {
            var pos = PositionSeconds;
            var sinceTick = (DateTime.UtcNow - _lastTickUtc).TotalSeconds;
            if (sinceTick > 0 && sinceTick < MaxInterpolationSeconds && !_dragging)
                pos = Math.Min(pos + sinceTick, DurationSeconds);
            return pos;
        }
    }

    private void EnsureBarsGeometry(double w, double h)
    {
        if (_barsGeometry != null && ReferenceEquals(_geoPeaks, _peaks) && _geoW == w && _geoH == h) return;

        var mid = h / 2;
        var amplitude = h / 2 - 3;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        var barCount = (int)((w - 2) / BarStep);
        for (var i = 0; i < barCount; i++)
        {
            var from = (int)((long)i * _peaks.Length / barCount);
            var to = (int)Math.Max(from + 1, (long)(i + 1) * _peaks.Length / barCount);
            var max = 0f;
            for (var j = from; j < to && j < _peaks.Length; j++)
                if (_peaks[j] > max) max = _peaks[j];

            var x = 1 + i * BarStep;
            var barH = Math.Max(2, max * amplitude);
            group.Children.Add(new RectangleGeometry(new Rect(x, mid - barH, BarWidth, barH * 2)));
        }
        group.Freeze();
        _barsGeometry = group;
        _geoPeaks = _peaks;
        _geoW = w;
        _geoH = h;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var mid = h / 2;
        var playedBrush = PlayedBrush ?? (Brush?)FindResourceThemed("AccentBrush") ?? Brushes.Transparent;
        var unplayedBrush = UnplayedBrush ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        var playheadBrush = PlayheadBrush ?? Brushes.White;

        var frac = DurationSeconds > 0.5
            ? Math.Clamp(DisplayPosition / DurationSeconds, 0, 1)
            : 0;
        var playheadX = _dragging ? _dragFrac * w : frac * w;
        _drawnPlayheadFrac = playheadX / Math.Max(1, w);

        // Empty track / peaks not ready yet: a flat line + playhead.
        if (_peaks.Length == 0)
        {
            dc.DrawRectangle(unplayedBrush, null, new Rect(0, mid - 1, w, 2));
            dc.DrawRectangle(playheadBrush, null, new Rect(playheadX - PlayheadWidth / 2, 1, PlayheadWidth, h - 2));
            return;
        }

        EnsureBarsGeometry(w, h);

        // The unplayed wave as a whole, with the played part on top via a clip (the bar
        // at the playhead colors smoothly, without a whole-bar step).
        dc.DrawGeometry(unplayedBrush, null, _barsGeometry);
        if (playheadX > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, playheadX, h)));
            dc.DrawGeometry(playedBrush, null, _barsGeometry);
            dc.Pop();
        }

        // Playhead.
        dc.DrawRectangle(playheadBrush, null, new Rect(playheadX - PlayheadWidth / 2, 1, PlayheadWidth, h - 2));
    }

    private static object? FindResourceThemed(string key)
    {
        try { return Application.Current?.TryFindResource(key); } catch { return null; }
    }

    // ========================= Mouse =========================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SeekCommand == null || DurationSeconds <= 0.5) return;
        _dragging = true;
        _dragFrac = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        CaptureMouse();
        _lastDragSeekTick = 0;
        SeekCommand.Execute(_dragFrac);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragFrac = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        InvalidateVisual();

        // Continuous seek while the button is held, throttled to ~150ms —
        // the final seek runs on release anyway.
        var now = Environment.TickCount64;
        if (now - _lastDragSeekTick < 150) return;
        _lastDragSeekTick = now;
        SeekCommand?.Execute(_dragFrac);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _dragFrac = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        SeekCommand?.Execute(_dragFrac);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Drag canceled (Alt+Tab etc.): the playhead returns to the position.
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
        }
    }
}
