using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BatPlayer.Controls;

/// <summary>
/// Smooth mouse-wheel scrolling: hooks PreviewMouseWheel on a ScrollViewer and animates
/// the vertical offset instead of an instant per-line jump. Enabled via an attached
/// property in the implicit ScrollViewer style.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    // This attached property is animated; each frame pulls the real offset along.
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    // Cache of a ScrollViewer's offset mode (pixels/items) — see IsPixelOffsetMode.
    private static readonly DependencyProperty PixelModeCacheProperty =
        DependencyProperty.RegisterAttached("PixelModeCache", typeof(bool?), typeof(SmoothScroll),
            new PropertyMetadata(null));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            WarmUpScrollPath(sv);
        }
        else sv.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    /// <summary>
    /// Warm up the smooth-scroll path right after the behavior attaches: otherwise the
    /// first glide of a page JITs the animation pipeline and the first MeasureWindow with
    /// an offset — the user caught a micro-freeze on the FIRST scroll of every page. An
    /// imperceptible 1px glide (1ms) does it before the first real wheel.
    /// </summary>
    private static void WarmUpScrollPath(ScrollViewer sv)
    {
        sv.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!sv.IsLoaded || sv.ScrollableHeight <= 1) return;
                sv.BeginAnimation(AnimatedOffsetProperty,
                    new DoubleAnimation(sv.VerticalOffset + 1, TimeSpan.FromMilliseconds(1)));
            }
            catch { /* window already closed — doesn't matter */ }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue);

    /// <summary>
    /// Offset mode: pixels (our VirtualizingWrapPanel with its own IScrollInfo, and regular
    /// ScrollViewers) or ITEMS (CanContentScroll on lists without a pixel IScrollInfo —
    /// e.g. the playlist's GridView track list). Previously a step of 192 went into item
    /// mode as 192 LINES per click — the list flew to the end.
    /// </summary>
    private static bool IsPixelOffsetMode(ScrollViewer sv)
    {
        var cached = (bool?)sv.GetValue(PixelModeCacheProperty);
        if (cached.HasValue) return cached.Value;

        var pixel = !sv.CanContentScroll || HasVirtualizingWrapPanel(sv);
        sv.SetValue(PixelModeCacheProperty, pixel);
        return pixel;
    }

    private static bool HasVirtualizingWrapPanel(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingWrapPanel) return true;
            if (HasVirtualizingWrapPanel(child)) return true;
        }
        return false;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0) return;

        // Long smooth glide: card re-realization every frame (the reason for the former
        // short 320ms) no longer costs anything — the panel doesn't re-measure valid cards
        // and BufferRows=2 realizes a row ahead. Same step (192px), lower speed —
        // scrolling "glides" instead of jerking.
        var pixel = IsPixelOffsetMode(sv);
        double target;
        double glideMs;
        if (pixel)
        {
            var lines = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
            target = Math.Clamp(sv.VerticalOffset - lines * 96.0 * 2, 0, sv.ScrollableHeight);
            glideMs = sv.CanContentScroll ? 480 : 520;
        }
        else
        {
            // item mode: 3 rows per click (like the default list wheel).
            target = Math.Clamp(sv.VerticalOffset - Math.Sign(e.Delta) * 3, 0, sv.ScrollableHeight);
            glideMs = 320;
        }

        // Snap the animation base to the current mid-animation offset, then retarget.
        sv.BeginAnimation(AnimatedOffsetProperty, null);
        sv.SetValue(AnimatedOffsetProperty, sv.VerticalOffset);

        sv.BeginAnimation(AnimatedOffsetProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(glideMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        e.Handled = true;
    }
}
