using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BatPlayer.Controls;

/// <summary>
/// Плавная прокрутка колеса мыши: перехватывает PreviewMouseWheel у ScrollViewer
/// и анимирует вертикальное смещение вместо мгновенного прыжка на строку.
/// Включается через attached-свойство в неявном стиле ScrollViewer.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    // Анимируем это attached-свойство; каждый кадр подтягиваем реальный offset.
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    // Кэш режима смещения конкретного ScrollViewer (пиксели/ items) — см. IsPixelOffsetMode.
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
    /// Прогрев пути плавного скролла сразу после подключения поведения: первый глайд
    /// страницы иначе JIT-ит анимационный конвейер и первый MeasureWindow со смещением —
    /// пользователь ловил микро-фриз на ПЕРВОМ прокруте каждой страницы. Незаметный
    /// глайд на 1px (1мс) делает это до первого реального колеса.
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
            catch { /* окно уже закрыто — неважно */ }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue);

    /// <summary>
    /// Режим смещения: пиксели (наш VirtualizingWrapPanel со своим IScrollInfo и
    /// обычные ScrollViewer) или ITEMS (CanContentScroll у списков без пиксельного
    /// IScrollInfo — например GridView-список треков плейлиста). Раньше шаг 192
    /// уходил в item-режим как 192 СТРОКИ за щелчок — список улетал в конец.
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

        // Длинный-плавный глайд: пере-реализация карточек каждый кадр (причина
        // прежних коротких 320мс) больше не стоит ничего — панель не перемеряет
        // валидные карточки, а BufferRows=2 реализует ряд загодя. Шаг прежний
        // (192px), скорость ниже — прокрутка «скользит», а не дёргается.
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
            // item-режим: 3 строки за щелчок (как дефолтный wheel у списков).
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
