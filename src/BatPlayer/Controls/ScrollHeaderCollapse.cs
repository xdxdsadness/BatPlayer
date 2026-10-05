using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BatPlayer.Controls;

/// <summary>
/// Сворачивание «шторки» заголовка страницы при прокрутке: скроллишь вниз — шторка
/// (заголовок + счётчик + кнопки шапки) плавно уезжает в ноль, контент занимает её
/// место и пролетает чуть выше прежней границы; вернулся наверх — шторка разворачивается.
/// Присоединяется к скроллируемому элементу (ListView/ScrollViewer), Header — сворачиваемый.
/// </summary>
public static class ScrollHeaderCollapse
{
    /// <summary>Шторка свернулась/развернулась (host, collapsed). Окно использует это,
    /// чтобы показывать действия страницы в тайтл-баре, пока шторка спрятана.</summary>
    public static event Action<FrameworkElement, bool>? CollapsedChanged;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.RegisterAttached("Header", typeof(FrameworkElement), typeof(ScrollHeaderCollapse),
            new PropertyMetadata(null, OnHeaderChanged));

    public static FrameworkElement GetHeader(DependencyObject d)
        => (FrameworkElement)d.GetValue(HeaderProperty);

    public static void SetHeader(DependencyObject d, FrameworkElement? value)
        => d.SetValue(HeaderProperty, value);

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        host.Loaded -= OnHostLoaded;
        host.Loaded += OnHostLoaded;
        // Уже загружен (шаблоны страниц пересоздаются при навигации) — вешаем сразу.
        if (host.IsLoaded) Attach(host);
    }

    private static void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host) Attach(host);
    }

    private static void Attach(FrameworkElement host)
    {
        var sv = FindScrollViewer(host);
        var header = GetHeader(host);
        if (sv == null || header == null) return;

        // host замыкается явно: состояние (NaturalHeight/Collapsed) живёт на host'е,
        // а sender'ом в ScrollChanged приходит сам ScrollViewer. Повторный Attach
        // (Loaded + разрешение биндинга приходят дважды на один host) больше не
        // подписывает ScrollChanged повторно — раньше лямбда копилась, и каждый
        // scroll-event обрабатывался по два раза.
        if ((bool)host.GetValue(AttachedProperty)) return;
        host.SetValue(AttachedProperty, true);
        sv.ScrollChanged += (_, args) => OnScrollChanged(host, args);

        // Естественные размеры шторки снимаются при первом сворачивании (страница
        // всегда загружается развёрнутой); хранятся в Property на host'е.
        if (host.GetValue(NaturalHeightProperty) is not double h || double.IsNaN(h))
            host.SetValue(NaturalHeightProperty, header.ActualHeight > 0 ? header.ActualHeight : double.NaN);
        if (host.GetValue(NaturalMarginProperty) is not Thickness m || m.Top == -1)
            host.SetValue(NaturalMarginProperty, header.Margin);
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("AttachGuard", typeof(bool), typeof(ScrollHeaderCollapse),
            new PropertyMetadata(false));

    private static readonly DependencyProperty NaturalHeightProperty =
        DependencyProperty.RegisterAttached("NaturalHeight", typeof(double), typeof(ScrollHeaderCollapse),
            new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty NaturalMarginProperty =
        DependencyProperty.RegisterAttached("NaturalMargin", typeof(Thickness), typeof(ScrollHeaderCollapse),
            new PropertyMetadata(new Thickness(0, -1, 0, 0)));

    private static readonly DependencyProperty CollapsedProperty =
        DependencyProperty.RegisterAttached("Collapsed", typeof(bool), typeof(ScrollHeaderCollapse),
            new PropertyMetadata(false));

    private static void OnScrollChanged(FrameworkElement host, ScrollChangedEventArgs e)
    {
        var header = GetHeader(host);
        if (header == null) return;

        var wantCollapsed = e.VerticalOffset > 14;
        if ((bool)host.GetValue(CollapsedProperty) == wantCollapsed) return;
        host.SetValue(CollapsedProperty, wantCollapsed);

        var naturalH = (double)host.GetValue(NaturalHeightProperty);
        var naturalM = (Thickness)host.GetValue(NaturalMarginProperty);
        if (double.IsNaN(naturalH)) return;

        header.ClipToBounds = true;
        // 300мс: сдвиг контента (шапка уступает место списку) растянут по всему
        // первому глайду колеса — иначе он сжимался в начало прокрутки и читался
        // как «проскок чуть дальше, чем должно».
        var duration = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var targetH = wantCollapsed ? 0d : naturalH;
        var targetM = wantCollapsed
            ? new Thickness(naturalM.Left, 0, naturalM.Right, 0)
            : naturalM;

        header.BeginAnimation(FrameworkElement.HeightProperty,
            new DoubleAnimation(header.ActualHeight, targetH, duration) { EasingFunction = ease });
        header.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(header.Margin, targetM, duration) { EasingFunction = ease });
        header.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(wantCollapsed ? 0d : 1d, duration) { EasingFunction = ease });
        header.IsHitTestVisible = !wantCollapsed;
        CollapsedChanged?.Invoke(header, wantCollapsed);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
