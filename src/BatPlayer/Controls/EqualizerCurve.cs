using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatPlayer.Models;

namespace BatPlayer.Controls;

/// <summary>
/// FabFilter Pro-Q style equalizer curve: log frequency scale 20 Hz…20 kHz, ±12 dB,
/// summed frequency response from biquad coefficients.
/// Gestures and shortcuts:
///  - double-click on the curve — ADD a node (Bell) at the click point;
///  - node drag — gain (vertical) and frequency (horizontal);
///  - double-click on a node — DELETE it;
///  - right-click on a node — change type: Bell → low cut → high cut;
///  - wheel over a Bell node — gain; over a cut — slope 12/18/24/30/36/48 dB/oct;
///  - wheel over empty curve — compress/stretch the WHOLE curve;
///  - Alt+drag — the same curve scaling with the mouse;
///  - Shift+drag — constrain movement to horizontal or vertical.
/// Anti-lag: while dragging, the picture is drawn from local state; values reach the
/// model/engine at most ~30 Hz (final values on release).
/// </summary>
public sealed class EqualizerCurve : FrameworkElement
{
    public const double MinDb = -12;
    public const double MaxDb = 12;
    public const double MinFreq = 20;
    public const double MaxFreq = 20000;
    private const double BandQ = 1.41;          // Bell width, as in EqualizerSampleProvider
    private const int CurveSteps = 300;
    private const double NodeHitRadius = 12;
    private const double EnginePushIntervalMs = 30; // model-write throttle while dragging

    // Curve math is computed at 48 kHz: filter shapes at these frequencies are
    // visually indistinguishable between 44.1 and 48 kHz.
    private const double ReferenceSampleRate = 48000;

    private const double PaddingLeft = 38, PaddingBottom = 22, PaddingTop = 8, PaddingRight = 10;

    // Pro-Q style grid: verticals at standard frequencies (all labeled),
    // horizontals every 3 dB (all labeled).
    private static readonly double[] GridFreqs = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private static readonly double[] GridDb = { -12, -9, -6, -3, 0, 3, 6, 9, 12 };

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands), typeof(System.Collections.IList), typeof(EqualizerCurve),
        new PropertyMetadata(null, OnBandsChanged));

    public System.Collections.IList? Bands
    {
        get => (System.Collections.IList?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    /// <summary>Double-click on empty curve space: add a band.</summary>
    public event EventHandler<(double Freq, double Gain)>? AddNodeRequested;
    /// <summary>Double-click on a node: remove the band (index into Bands).</summary>
    public event EventHandler<int>? RemoveNodeRequested;

    private int _hoverIndex = -1;
    private int _dragIndex = -1;
    private bool _altScale;          // Alt: scale the whole curve
    private int _lockAxis;           // Shift: 0 = free, 1 = horizontal, 2 = vertical
    private double _dragStartY;
    private Point _dragStartPoint;
    private double[] _scaleStartGains = Array.Empty<double>();
    private double _scaleFactor = 1;
    private int _lastCreatedIndex = -1;      // node created by a single click
    private long _lastCreatedTicks;          // guard: a double-click on it doesn't remove it

    // Local state of the dragged node: drawn from it EVERY frame; the model/engine is
    // written throttled (the lag source was the synchronous model write and filter
    // recompute on every mouse event).
    private double _dragFreq, _dragGain;
    private readonly Stopwatch _pushThrottle = new();

    // Cache of static grid labels.
    private double _labelCacheWidth = -1, _labelCacheHeight = -1, _labelCacheDpi = -1;
    private List<(FormattedText Text, Point Pos)>? _labelCache;

    public EqualizerCurve()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    /// <summary>
    /// A FrameworkElement without a background is invisible to WPF hit-testing: wheel,
    /// clicks and hover over empty curve areas never reached the control. Return ourselves
    /// for any point — the whole plot is one interactive surface.
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
        => new PointHitTestResult(this, parameters.HitPoint);

    private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not EqualizerCurve c) return;
        c.ObserveCollection(e.NewValue as System.Collections.IList);
        c.InvalidateVisual();
    }

    // Band collection observation: presets/reset change the set itself (Clear/Add);
    // without a CollectionChanged subscription the control never learns of it and the
    // curve "freezes" empty.
    private INotifyCollectionChanged? _observedCollection;
    private readonly HashSet<INotifyPropertyChanged> _observedBands = new();

    private void ObserveCollection(System.Collections.IList? list)
    {
        if (_observedCollection != null)
        {
            _observedCollection.CollectionChanged -= OnBandsCollectionChanged;
            _observedCollection = null;
        }
        _observedBands.Clear();

        if (list is INotifyCollectionChanged ncc)
        {
            _observedCollection = ncc;
            ncc.CollectionChanged += OnBandsCollectionChanged;
        }
        HookAll(list);
        InvalidateVisual();
    }

    private void OnBandsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Any structural change — rescan subscriptions and redraw.
        _observedBands.Clear();
        HookAll(Bands);
        InvalidateVisual();
    }

    private void HookAll(System.Collections.IEnumerable? items)
    {
        if (items == null) return;
        foreach (var b in items)
            if (b is INotifyPropertyChanged inpc && _observedBands.Add(inpc))
                inpc.PropertyChanged += OnBandChanged;
    }

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ===== Coordinates =====

    private Rect PlotRect
    {
        get
        {
            var w = ActualWidth - PaddingLeft - PaddingRight;
            var h = ActualHeight - PaddingTop - PaddingBottom;
            return new Rect(PaddingLeft, PaddingTop, Math.Max(10, w), Math.Max(10, h));
        }
    }

    private static double FreqToX(double freq, Rect r)
        => r.X + r.Width * (Math.Log10(freq / MinFreq) / Math.Log10(MaxFreq / MinFreq));

    private static double XToFreq(double x, Rect r)
        => MinFreq * Math.Pow(10, Math.Clamp((x - r.X) / r.Width, 0, 1) * Math.Log10(MaxFreq / MinFreq));

    private static double GainToY(double gain, Rect r)
        => r.Y + (MaxDb - Math.Clamp(gain, MinDb, MaxDb)) / (MaxDb - MinDb) * r.Height;

    private static double YToGain(double y, Rect r)
        => Math.Clamp(MaxDb - (y - r.Y) / r.Height * (MaxDb - MinDb), MinDb, MaxDb);

    /// <summary>Node position: cuts live on the 0 dB line (they have no gain).</summary>
    private double NodeGain(EqualizerBand b) => b.Type == EqualizerBandType.Bell ? b.Gain : 0;

    private int HitTest(Point p)
    {
        if (Bands == null) return -1;
        var r = PlotRect;
        for (int i = 0; i < Bands.Count; i++)
        {
            if (Bands[i] is not EqualizerBand b) continue;
            var c = new Point(FreqToX(b.Frequency, r), GainToY(NodeGain(b), r));
            if ((p - c).LengthSquared <= NodeHitRadius * NodeHitRadius) return i;
        }
        return -1;
    }

    // ===== Rendering =====

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("SecondaryBrush") as Brush ?? Brushes.Transparent;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var r = PlotRect;
        // Visible grid: semi-transparent white lines on the dark theme.
        var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1);
        var faintPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), 0.7);
        zeroPen.Freeze();
        faintPen.Freeze();
        var textBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.MediumPurple;
        var nodeStroke = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        var dpi = VisualTreeHelper.GetDpi(this);

        EnsureLabelCache(dpi);
        foreach (var (text, pos) in _labelCache!)
            dc.DrawText(text, pos);

        // Grid lines: verticals by frequency, horizontals by dB, zero is brighter.
        foreach (var f in GridFreqs)
        {
            var x = FreqToX(f, r);
            dc.DrawLine(faintPen, new Point(x, r.Top), new Point(x, r.Bottom));
        }
        foreach (var db in GridDb)
        {
            if (db == 0) continue;
            var y = GainToY(db, r);
            dc.DrawLine(faintPen, new Point(r.Left, y), new Point(r.Right, y));
        }
        dc.DrawLine(zeroPen, new Point(r.Left, GainToY(0, r)), new Point(r.Right, GainToY(0, r)));

        // Summed frequency response: bands summed in dB (the standard for EQ visualization).
        var points = new Point[CurveSteps + 1];
        for (int i = 0; i <= CurveSteps; i++)
        {
            var freq = MinFreq * Math.Pow(MaxFreq / MinFreq, i / (double)CurveSteps);
            var db = 0.0;
            if (Bands != null)
                foreach (var b in Bands)
                    if (b is EqualizerBand band)
                        db += BandResponseDb(freq, EffectiveFreq(band), EffectiveGain(band), band.Type, band.SlopeDbOct, band.Q);
            points[i] = new Point(FreqToX(freq, r), GainToY(db, r));
        }

        // Semi-transparent fill between the curve and the zero line (as in Pro-Q).
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], true, false);
            ctx.LineTo(new Point(points[^1].X, GainToY(0, r)), true, false);
            ctx.LineTo(new Point(points[0].X, GainToY(0, r)), true, false);
        }
        fillGeo.Freeze();
        var fillBrush = accent.Clone();
        fillBrush.Opacity = 0.18;
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fillGeo);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], true, false);
        }
        geo.Freeze();
        var curvePen = new Pen(accent, 2.2);
        curvePen.Freeze();
        dc.DrawGeometry(null, curvePen, geo);

        // Nodes + thin lines to zero. Bell is a numbered circle, cuts are triangles.
        for (int i = 0; i < (Bands?.Count ?? 0); i++)
        {
            if (Bands![i] is not EqualizerBand b) continue;
            var c = new Point(FreqToX(b.Frequency, r), GainToY(NodeGain(b), r));
            dc.DrawLine(faintPen, new Point(c.X, GainToY(0, r)), c);

            var active = i == _hoverIndex || i == _dragIndex;
            switch (b.Type)
            {
                case EqualizerBandType.LowCut:
                    // Low cut: cuts everything left of the node — triangle points right.
                    dc.DrawGeometry(accent, NodePen(nodeStroke, active), Triangle(c, +1));
                    break;
                case EqualizerBandType.HighCut:
                    // High cut: cuts everything right of the node — triangle points left.
                    dc.DrawGeometry(accent, NodePen(nodeStroke, active), Triangle(c, -1));
                    break;
                default:
                {
                    var radius = active ? 8.5 : 7;
                    dc.DrawEllipse(accent, NodePen(nodeStroke, active), c, radius, radius);
                    var num = Fmt((i + 1).ToString(), 8, nodeStroke, dpi);
                    dc.DrawText(num, new Point(c.X - num.Width / 2, c.Y - num.Height / 2));
                    break;
                }
            }

            // "Listen to harmonic" solo — dashed ring around the node.
            if (b.IsSolo)
            {
                var soloPen = new Pen(Brushes.White, 1.6) { DashStyle = DashStyles.Dash };
                soloPen.Freeze();
                dc.DrawEllipse(null, soloPen, c, 13, 13);
            }
        }

        // Tooltip above the dragged/hovered node.
        var hi = _dragIndex >= 0 ? _dragIndex : _hoverIndex;
        if (hi >= 0 && hi < (Bands?.Count ?? 0) && Bands![hi] is EqualizerBand hb)
        {
            var effGain = hi == _dragIndex ? _dragGain : hb.Gain;
            var text = hb.Type switch
            {
                EqualizerBandType.LowCut => $"{FreqLabel(EffectiveFreq(hb))}  {Localization.Loc.Get("EqLowCut")} {EffectiveSlope(hb)} dB/oct",
                EqualizerBandType.HighCut => $"{FreqLabel(EffectiveFreq(hb))}  {Localization.Loc.Get("EqHighCut")} {EffectiveSlope(hb)} dB/oct",
                _ => $"{FreqLabel(EffectiveFreq(hb))}   {(effGain >= 0 ? "+" : "")}{effGain:0.#} dB   Q {hb.Q:0.##}"
            };
            var ft = Fmt(text, 11, nodeStroke, dpi);
            var tp = new Point(
                Math.Clamp(FreqToX(EffectiveFreq(hb), r) - ft.Width / 2, 4, ActualWidth - ft.Width - 4),
                Math.Max(2, GainToY(NodeGain(hb), r) - 26));
            dc.DrawText(ft, tp);
        }
    }

    private static Pen NodePen(Brush brush, bool active)
    {
        var pen = new Pen(brush, active ? 2 : 1.2);
        pen.Freeze();
        return pen;
    }

    private static StreamGeometry Triangle(Point center, double dir)
    {
        const double s = 9;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(center.X - dir * s, center.Y - s * 0.8), true, true);
            ctx.LineTo(new Point(center.X - dir * s, center.Y + s * 0.8), true, false);
            ctx.LineTo(new Point(center.X + dir * s, center.Y), true, false);
        }
        geo.Freeze();
        return geo;
    }

    // While dragging, the node is drawn from local values (anti-lag).
    private double EffectiveFreq(EqualizerBand b) => ReferenceEquals(b, DraggedBand) && _dragIndex >= 0 ? _dragFreq : b.Frequency;
    private double EffectiveGain(EqualizerBand b) => ReferenceEquals(b, DraggedBand) && _dragIndex >= 0 ? _dragGain : NodeGain(b);
    private int EffectiveSlope(EqualizerBand b) => b.SlopeDbOct;
    private EqualizerBand? DraggedBand => _dragIndex >= 0 && _dragIndex < (Bands?.Count ?? 0) ? Bands![_dragIndex] as EqualizerBand : null;

    private void EnsureLabelCache(DpiScale dpi)
    {
        if (_labelCache != null
            && Math.Abs(_labelCacheWidth - ActualWidth) < 0.5
            && Math.Abs(_labelCacheHeight - ActualHeight) < 0.5
            && Math.Abs(_labelCacheDpi - dpi.PixelsPerDip) < 0.01)
            return;

        _labelCacheWidth = ActualWidth;
        _labelCacheHeight = ActualHeight;
        _labelCacheDpi = dpi.PixelsPerDip;

        _labelCache = new List<(FormattedText, Point)>();
        var r = PlotRect;
        var textBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

        foreach (var f in GridFreqs)
        {
            var x = FreqToX(f, r);
            _labelCache.Add((Fmt(FreqLabel(f), 10, textBrush, dpi), new Point(x - 12, r.Bottom + 4)));
        }
        foreach (var db in GridDb)
        {
            var y = GainToY(db, r);
            _labelCache.Add((Fmt(db > 0 ? "+" + db.ToString("0") : db.ToString("0"), 10, textBrush, dpi),
                new Point(6, y - 7)));
        }
    }

    /// <summary>Band filter magnitude in dB at frequency freq.</summary>
    private static double BandResponseDb(double freq, double centerHz, double gainDb,
        EqualizerBandType type, int slopeDbOct, double q)
    {
        switch (type)
        {
            case EqualizerBandType.LowCut:
            case EqualizerBandType.HighCut:
            {
                // Cascade of N/12 identical biquads: in dB the response multiplies by N.
                // A 6 dB/oct tail (slopes 18/30) — a first-order section.
                // LowCut is an HPF (removes lows), HighCut is an LPF: don't mix them up.
                var stages = Math.Max(1, slopeDbOct / 12);
                var kind = type == EqualizerBandType.HighCut ? BiquadKind.LowPass : BiquadKind.HighPass;
                var db = stages * BiquadMagnitudeDb(freq, centerHz, kind, 0);
                if (slopeDbOct % 12 == 6)
                    db += FirstOrderMagnitudeDb(freq, centerHz, type == EqualizerBandType.LowCut);
                return db;
            }
            default:
            {
                if (Math.Abs(gainDb) < 1e-3) return 0;
                return BiquadMagnitudeDb(freq, centerHz, BiquadKind.Peaking, gainDb, q);
            }
        }
    }

    private enum BiquadKind { Peaking, LowPass, HighPass }

    /// <summary>Response of the first-order section (6 dB/oct) the audio engine uses to reach
    /// fractional slopes 18/30: analog LPF/HPF — minus/plus 3 dB at the cutoff frequency.</summary>
    private static double FirstOrderMagnitudeDb(double freq, double centerHz, bool highPass)
    {
        var x = freq / centerHz;
        return highPass
            ? 5 * Math.Log10(x * x / (1 + x * x))
            : -5 * Math.Log10(1 + x * x);
    }

    /// <summary>Response of one biquad: peaking (with gain and Q) or Butterworth HP/LP.</summary>
    private static double BiquadMagnitudeDb(double freq, double centerHz, BiquadKind kind, double gainDb, double q = 1.41)
    {
        var w0 = 2 * Math.PI * centerHz / ReferenceSampleRate;
        double b0, b1, b2, a0, a1, a2;

        switch (kind)
        {
            case BiquadKind.HighPass:
            {
                // RBJ high-pass, Butterworth Q = 0.7071 (same as the audio HighPassFilter).
                var alpha = Math.Sin(w0) / (2 * 0.7071);
                var cosw0 = Math.Cos(w0);
                b0 = (1 + cosw0) / 2; b1 = -(1 + cosw0); b2 = (1 + cosw0) / 2;
                a0 = 1 + alpha; a1 = -2 * cosw0; a2 = 1 - alpha;
                break;
            }
            case BiquadKind.LowPass:
            {
                var alpha = Math.Sin(w0) / (2 * 0.7071);
                var cosw0 = Math.Cos(w0);
                b0 = (1 - cosw0) / 2; b1 = 1 - cosw0; b2 = (1 - cosw0) / 2;
                a0 = 1 + alpha; a1 = -2 * cosw0; a2 = 1 - alpha;
                break;
            }
            default:
            {
                var a = Math.Pow(10, gainDb / 40);
                var alpha = Math.Sin(w0) / (2 * Math.Max(0.1, q));
                var cosw0 = Math.Cos(w0);
                b0 = 1 + alpha * a; b1 = -2 * cosw0; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cosw0; a2 = 1 - alpha / a;
                break;
            }
        }

        var w = 2 * Math.PI * freq / ReferenceSampleRate;
        var cw = Math.Cos(w);
        var cw2 = Math.Cos(2 * w);
        var num = b0 * b0 + b1 * b1 + b2 * b2 + 2 * b1 * (b0 + b2) * cw + 2 * b0 * b2 * cw2;
        var den = a0 * a0 + a1 * a1 + a2 * a2 + 2 * a1 * (a0 + a2) * cw + 2 * a0 * a2 * cw2;
        return 10 * Math.Log10(num / den);
    }

    private FormattedText Fmt(string text, double size, Brush brush, DpiScale dpi)
    {
        // FrameworkElement has no FontFamily — take the app font from resources.
        var family = TryFindResource("AppFont") as FontFamily ?? SystemFonts.MessageFontFamily;
        return new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            size, brush, dpi.PixelsPerDip);
    }

    private static string FreqLabel(double f)
        => f >= 1000 ? $"{f / 1000:0.#} kHz" : $"{f:0} Hz";

    // ===== Interaction =====

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var r = PlotRect;

        if (_dragIndex >= 0)
        {
            if (_altScale)
            {
                // Alt: scale the WHOLE curve — Bell band gains are multiplied by a factor
                // from the vertical mouse shift (compress/stretch, as in Pro-Q).
                var shiftDb = YToGain(p.Y, r) - YToGain(_dragStartY, r);
                _scaleFactor = Math.Pow(10, shiftDb / 20);
                PushModelThrottled();
                InvalidateVisual();
                return;
            }

            var newFreq = XToFreq(p.X, r);
            var newGain = YToGain(p.Y, r);

            // Shift: lock the axis on the first noticeable shift.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lockAxis == 0)
                _lockAxis = Math.Abs(p.X - _dragStartPoint.X) >= Math.Abs(p.Y - _dragStartPoint.Y) ? 1 : 2;
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _lockAxis = 0;

            if (_lockAxis == 1) newGain = _dragGain;          // frequency only
            else if (_lockAxis == 2) newFreq = _dragFreq;     // gain only

            // Cuts have no gain: the node lives on the 0 dB line.
            _dragFreq = newFreq;
            _dragGain = DraggedBand is { Type: not EqualizerBandType.Bell } ? 0 : newGain;
            PushModelThrottled();
            InvalidateVisual();
            return;
        }

        var hit = HitTest(p);
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Cross;
            InvalidateVisual();
        }
    }

    /// <summary>Push the local drag state to the model/engine at most ~30 Hz.</summary>
    private void PushModelThrottled()
    {
        if (_pushThrottle.IsRunning && _pushThrottle.ElapsedMilliseconds < EnginePushIntervalMs) return;
        _pushThrottle.Restart();
        CommitDragToModel();
    }

    private void CommitDragToModel()
    {
        if (_dragIndex < 0) return;

        if (_altScale)
        {
            for (int i = 0; i < Bands!.Count && i < _scaleStartGains.Length; i++)
                if (Bands[i] is EqualizerBand b && b.Type == EqualizerBandType.Bell)
                    b.Gain = Math.Clamp(Math.Round(_scaleStartGains[i] * _scaleFactor, 1), MinDb, MaxDb);
            return;
        }

        if (DraggedBand is not EqualizerBand band) return;
        band.Frequency = Math.Round(_dragFreq, 1);
        if (band.Type == EqualizerBandType.Bell)
            band.Gain = Math.Round(_dragGain, 1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = HitTest(p);

        // Double-click on an existing node — remove it (except one just created).
        if (e.ClickCount == 2 && hit >= 0)
        {
            var justCreated = hit == _lastCreatedIndex && Environment.TickCount64 - _lastCreatedTicks < 500;
            if (!justCreated)
                RemoveNodeRequested?.Invoke(this, hit);
            e.Handled = true;
            return;
        }

        if (hit < 0)
        {
            // Pro-Q mechanics: a single click on empty curve space places a node at the
            // click point and immediately starts dragging it (same gesture).
            var countBefore = Bands?.Count ?? 0;
            AddNodeRequested?.Invoke(this, (Math.Round(XToFreq(p.X, PlotRect), 1), Math.Round(YToGain(p.Y, PlotRect), 1)));
            var countAfter = Bands?.Count ?? 0;
            hit = countAfter - 1;
            if (countAfter <= countBefore || hit < 0)
            {
                // Nothing was added (band limit).
                e.Handled = true;
                return;
            }
            _lastCreatedIndex = hit;
            _lastCreatedTicks = Environment.TickCount64;
            if (Bands![hit] is EqualizerBand nb)
            {
                _dragFreq = nb.Frequency;
                _dragGain = nb.Gain;
            }
        }
        else
        {
            _lastCreatedIndex = -1;
        }

        _dragIndex = hit;
        _dragStartY = p.Y;
        _dragStartPoint = p;
        _altScale = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        _lockAxis = 0;

        if (DraggedBand is EqualizerBand b)
        {
            _dragFreq = b.Frequency;
            _dragGain = b.Gain;
        }

        if (_altScale)
        {
            _scaleStartGains = new double[Bands?.Count ?? 0];
            for (int i = 0; i < _scaleStartGains.Length; i++)
                if (Bands![i] is EqualizerBand sb) _scaleStartGains[i] = sb.Gain;
        }

        _pushThrottle.Restart();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragIndex < 0) return;
        CommitDragToModel(); // final values reliably reach the model/engine
        _dragIndex = -1;
        _altScale = false;
        _lockAxis = 0;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// The wheel is handled in the PREVIEW phase to guarantee the page under the curve
    /// never scrolls: over a Bell node — gain; Ctrl+wheel — width (Q); over a cut —
    /// slope 12/18/24/30/36/48 dB/oct; over empty curve — compress/stretch the whole curve.
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var notch = e.Delta > 0 ? 1 : -1;
        var hit = HitTest(e.GetPosition(this));

        if (hit >= 0 && Bands?[hit] is EqualizerBand b)
        {
            if (b.Type == EqualizerBandType.Bell)
            {
                // As in Pro-Q: wheel over a node — bump width (Q), Ctrl+wheel — gain.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    b.Gain = Math.Clamp(b.Gain + notch * 0.5, MinDb, MaxDb);
                else
                    b.Q = Math.Clamp(b.Q * (notch > 0 ? 1.15 : 1 / 1.15), 0.3, 8);
            }
            else
                CycleSlope(b);
            return;
        }

        // Scale the whole curve: Bell band gains are multiplied by the factor.
        var factor = Math.Pow(10, notch * 0.5 / 20);
        if (Bands == null) return;
        foreach (var item in Bands)
            if (item is EqualizerBand band && band.Type == EqualizerBandType.Bell)
                band.Gain = Math.Clamp(Math.Round(band.Gain * factor, 1), MinDb, MaxDb);
    }

    // 6 dB/oct steps: 12/18/24/30/36/48. Fractional twelves (18/30) are assembled in the
    // audio engine as a biquad cascade + first-order section (see BuildBand).
    private static void CycleSlope(EqualizerBand b)
        => b.SlopeDbOct = b.SlopeDbOct switch { 12 => 18, 18 => 24, 24 => 30, 30 => 36, 36 => 48, _ => 12 };

    /// <summary>Right-click on a node — menu: band type, "listen to harmonic" solo, delete.</summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit >= 0 && Bands?[hit] is EqualizerBand b)
        {
            ShowNodeMenu(b, hit);
            e.Handled = true;
            return;
        }
        base.OnMouseRightButtonDown(e);
    }

    private void ShowNodeMenu(EqualizerBand band, int index)
    {
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PlacementTarget = this,
            Background = TryFindResource("PanelElevatedBrush") as Brush ?? Brushes.White,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black,
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray
        };

        foreach (var (type, headerKey) in new[]
                 {
                     (EqualizerBandType.Bell, "EqBell"),
                     (EqualizerBandType.LowCut, "EqLowCut"),
                     (EqualizerBandType.HighCut, "EqHighCut")
                 })
        {
            var t = type;
            var mi = new MenuItem
            {
                Header = Localization.Loc.Get(headerKey),
                IsChecked = band.Type == t,
                Background = menu.Background,
                Foreground = menu.Foreground
            };
            mi.Click += (_, _) => band.Type = t;
            menu.Items.Add(mi);
        }

        var solo = new MenuItem
        {
            Header = Localization.Loc.Get("EqSolo"),
            IsChecked = band.IsSolo,
            Background = menu.Background,
            Foreground = menu.Foreground
        };
        solo.Click += (_, _) => band.IsSolo = !band.IsSolo;
        menu.Items.Add(solo);

        var delete = new MenuItem
        {
            Header = Localization.Loc.Get("EqDelete"),
            Background = menu.Background,
            Foreground = menu.Foreground
        };
        delete.Click += (_, _) => RemoveNodeRequested?.Invoke(this, index);
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }
}
