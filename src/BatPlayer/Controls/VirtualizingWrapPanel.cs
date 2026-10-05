using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Виртуализирующая панель-сетка: фиксированное число колонок равной ширины, высота строки —
/// по самому высокому ребёнку окна. Замена UniformGrid в списке карточек (Library/SoundCloud/
/// YM/VK/Artists/Downloads/ArtistProfile): UniformGrid не виртуализирует — сотни карточек
/// реализуются все сразу (память + лаг при навигации), а здесь создаются только строки
/// видимой области ± буфер: обложки загружаются лениво, «по мере скролла».
///
/// Реализация по канону VirtualizingPanel + IScrollInfo: ScrollViewer (CanContentScroll)
/// делегирует скролл панели; экстент = строки × высота строки; контейнеры ре-реализуются
/// под окно видимости через ItemContainerGenerator. Вертикальный скролл — пиксельный.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    // ========================= Настройки =========================

    /// <summary>Число колонок сетки (как Columns у UniformGrid).</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(VirtualizingWrapPanel),
        new PropertyMetadata(4, OnGeometryInvalidated));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Строк выше/ниже видимой области держим реализованными (запас при скролле).
    /// 2, а не 1: ряд реализуется ЗАГОДЯ до въезда в кадр — длинный плавный глайд
    /// SmoothScroll не спотыкается о создание тяжёлого шаблона карточки на лету.</summary>
    private const int BufferRows = 2;

    private const double ScrollLineDelta = 16.0;
    private const double WheelLinesPerTick = 3.0;

    // ===== Плавный (анимированный) скролл =====
    // Колесо/построчный скролл не прыгают мгновенно: цель пишется в _targetOffsetY,
    // а _offsetY догоняет её экспоненциально в таймере ~60 fps — контент «скользит»,
    // а не дёргается. Прямые установки offset (скроллбар, MakeVisible, сбросы
    // коллекции) идут мимо анимации: и offset, и цель выставляются сразу.
    /// <summary>Доля приближения к цели за тик: 0.35 — дистанция схлопывается за ~4-5
    /// кадров (~70мс), движение остаётся быстрым, но с видимой инерцией.</summary>
    private const double SmoothScrollLerp = 0.35;
    /// <summary>Ближе этого (px) к цели — доезжаем мгновенно и останавливаем таймер.</summary>
    private const double SmoothScrollSnap = 0.5;
    /// <summary>Период тика анимации ~ один кадр при 60 Гц.</summary>
    private const int SmoothScrollIntervalMs = 16;

    /// <summary>Оценка высоты строки до первого измерения: карточка с квадратной обложкой
    /// ≈ ширина ячейки + подписи. После первого измерения уточняется фактической высотой.</summary>
    private const double InitialRowHeightFactor = 1.2;

    /// <summary>Оценка вьюпорта до первого подключения ScrollViewer (px):
    /// достаточно для первых рядов, не даёт реализовать весь список.</summary>
    private const double InitialEstimatedViewport = 720.0;

    // ======================= Состояние ===========================

    private ScrollViewer? _scrollOwner;

    private double _offsetY;                 // вертикальный скролл в пикселях
    private double _targetOffsetY;           // цель плавного скролла (колесо/построчно)
    private System.Windows.Threading.DispatcherTimer? _smoothScrollTimer;
    private double _viewportHeight;
    private double _extentHeight;
    private double _itemWidth;               // ширина ячейки = viewport / Columns
    private double _lastItemWidth = double.NaN;
    private double _rowHeight = double.NaN;  // высота строки; NaN — переоценить
    // Ширина, которой карточки мерились последний раз: скролл-таймер дёргает
    // InvalidateMeasure ~60 раз/с, и без этого поля каждый тик ПЕРЕМЕРИВАЛ все
    // реализованные карточки (десятки тяжёлых шаблонов на кадр — главный источник
    // рывков при скролле больших библиотек). Дети с валидным measure и той же
    // шириной пропускаются; любое изменение содержимого карточки само сбрасывает
    // её IsMeasureValid → она перемеряется штатно.
    private double _measuredChildWidth = double.NaN;

    /// <summary>
    /// Владелец панели: ближайший ItemsControl вверх по визуальному дереву
    /// (ListView и есть предок: ListView → Border → ScrollViewer → … →
    /// ItemsPresenter → панель). ВАЖНО: ItemsControl.ItemsControlFromItemContainer
    /// для самой панели-хоста возвращает null всегда (STA-зонд подтвердил даже
    /// для штатного VirtualizingStackPanel) — генератор панели подключается
    /// только через ItemsPresenter, а ItemsControl надёжнее искать по дереву.
    /// </summary>
    private ItemsControl? FindItemsOwner()
    {
        DependencyObject d = this;
        while (d != null)
        {
            if (d is ItemsControl ic && ic.ItemContainerGenerator != null) return ic;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>
    /// Коллекция элементов владельца. Пока панель не вставлена в дерево
    /// ItemsControl-а (первый measure может случиться раньше), ведём себя
    /// как пустая панель — вместо падения всего layout-а.
    /// ВАЖНО: уведомления генератора WPF доставляет только штатным панелям
    /// (STA-зонд: у нашей панели VirtualizingPanel.ItemContainerGenerator
    /// навсегда null), поэтому изменения Items слушаем напрямую —
    /// ItemCollection реализует INotifyCollectionChanged. Без этого
    /// Clear/Add карточек при навигации не перерисовывали бы панель.
    /// </summary>
    private System.Windows.Controls.ItemCollection? _subscribedItems;

    private bool TryGetItems(out System.Windows.Controls.ItemCollection items)
    {
        var owner = FindItemsOwner();
        if (owner != null)
        {
            items = owner.Items;
            if (!ReferenceEquals(_subscribedItems, items))
            {
                DetachItemsSubscription();
                _subscribedItems = items;
                ((INotifyCollectionChanged)items).CollectionChanged += OnItemsCollectionChanged;
            }
            return true;
        }
        items = null!;
        return false;
    }

    /// <summary>Изменение Items (Clear/Add при навигации и синках): сброс окна и перелейаут.</summary>
    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
            _rowHeight = double.NaN;
            ResetScrollOffset();
        }
        _scrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private void DetachItemsSubscription()
    {
        if (_subscribedItems != null)
        {
            ((INotifyCollectionChanged)_subscribedItems).CollectionChanged -= OnItemsCollectionChanged;
            _subscribedItems = null;
        }
    }

    private ItemContainerGenerator? _panelGenerator;

    /// <summary>
    /// Генератор, привязанный к этой панели. Приоритет — штатный
    /// VirtualizingPanel.ItemContainerGenerator (его подключает ItemsPresenter
    /// у штатных панелей); если он не пришёл — создаём панельный view сами
    /// через публичный IItemContainerGenerator.GetItemContainerGeneratorForPanel.
    /// ВАЖНО: «сырой» ItemContainerGenerator владельца использовать нельзя —
    /// у него своя сессия генерации, и GenerateNext с ним падает
    /// («Must call GenerateNext while content generation is in progress»).
    /// </summary>
    private ItemContainerGenerator? PanelGenerator
    {
        get
        {
            if (_panelGenerator != null) return _panelGenerator;
            if (ItemContainerGenerator is ItemContainerGenerator own)
            {
                _panelGenerator = own;
                return own;
            }
            var owner = FindItemsOwner();
            if (owner == null) return null;
            _panelGenerator = (ItemContainerGenerator)((IItemContainerGenerator)owner.ItemContainerGenerator)
                .GetItemContainerGeneratorForPanel(this);
            return _panelGenerator;
        }
    }

    /// <summary>Ближайший ItemsControl над элементом (контейнером карточки).</summary>
    private static ItemsControl? FindItemsOwnerFrom(DependencyObject start)
    {
        DependencyObject d = start;
        while (d != null)
        {
            if (d is ItemsControl ic && ic.ItemContainerGenerator != null) return ic;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ========================= IScrollInfo =======================

    public ScrollViewer ScrollOwner { get => _scrollOwner!; set => _scrollOwner = value; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offsetY;
    public double ExtentWidth => ViewportWidth;
    public double ExtentHeight => _extentHeight;
    public double ViewportWidth => ActualWidth;
    public double ViewportHeight => _viewportHeight;

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element
            && FindItemsOwnerFrom(element) is { } owner
            && Columns > 0 && !double.IsNaN(_rowHeight) && _rowHeight > 0)
        {
            var index = PanelGenerator?.IndexFromContainer(element) ?? -1;
            if (index >= 0)
            {
                var top = index / Columns * _rowHeight;
                if (top < _offsetY) SetVerticalOffset(top);
                else if (top + _rowHeight > _offsetY + _viewportHeight)
                    SetVerticalOffset(top + _rowHeight - _viewportHeight);
            }
        }
        return rectangle;
    }

    public void LineUp() => SmoothScrollTo(_targetOffsetY - ScrollLineDelta);
    public void LineDown() => SmoothScrollTo(_targetOffsetY + ScrollLineDelta);
    public void LineLeft() { }
    public void LineRight() { }

    public void MouseWheelUp() => SmoothScrollTo(_targetOffsetY - ScrollLineDelta * WheelLinesPerTick);
    public void MouseWheelDown() => SmoothScrollTo(_targetOffsetY + ScrollLineDelta * WheelLinesPerTick);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public void PageUp() => SetVerticalOffset(_offsetY - _viewportHeight);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewportHeight);
    public void PageLeft() { }
    public void PageRight() { }

    public void SetHorizontalOffset(double offset) { /* горизонтального скролла нет */ }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extentHeight - _viewportHeight);
        var clamped = Math.Max(0, Math.Min(offset, max));
        // Прямая установка (скроллбар, MakeVisible, сброс при изменении коллекции):
        // без анимации — гасим таймер и синхронно ставим offset и цель.
        StopSmoothScroll();
        if (Math.Abs(clamped - _offsetY) < 0.5 && Math.Abs(clamped - _targetOffsetY) < 0.5) return;
        _offsetY = clamped;
        _targetOffsetY = clamped;
        InvalidateMeasure();
        _scrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Задать цель плавного скролла (колесо/построчно): кламп по экстенту,
    /// таймер анимации запускается лениво и переиспользуется (панель живёт долго —
    /// таймер один на всю жизнь панели, не плодится на каждый скролл).</summary>
    private void SmoothScrollTo(double target)
    {
        var max = Math.Max(0, _extentHeight - _viewportHeight);
        _targetOffsetY = Math.Max(0, Math.Min(target, max));
        if (_smoothScrollTimer == null)
        {
            _smoothScrollTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(SmoothScrollIntervalMs),
                System.Windows.Threading.DispatcherPriority.Render,
                OnSmoothScrollTick, Dispatcher);
        }
        _smoothScrollTimer.Start();
    }

    /// <summary>Тик анимации: _offsetY экспоненциально догоняет цель; на дистанции
    /// меньше порога — доезжаем и останавливаемся. Кламп цели по экстенту закрывает
    /// случай, когда список сократился в середине анимации.</summary>
    private void OnSmoothScrollTick(object? sender, EventArgs e)
    {
        var max = Math.Max(0, _extentHeight - _viewportHeight);
        if (_targetOffsetY > max) _targetOffsetY = max;

        var diff = _targetOffsetY - _offsetY;
        if (Math.Abs(diff) < SmoothScrollSnap)
        {
            _offsetY = _targetOffsetY;
            _smoothScrollTimer?.Stop();
        }
        else
        {
            _offsetY += diff * SmoothScrollLerp;
        }

        InvalidateMeasure();
        _scrollOwner?.InvalidateScrollInfo();
    }

    private void StopSmoothScroll()
    {
        if (_smoothScrollTimer is { IsEnabled: true })
            _smoothScrollTimer.Stop();
    }

    /// <summary>Сброс скролла в начало без анимации (сбросы коллекции, пересборка списка).</summary>
    private void ResetScrollOffset()
    {
        StopSmoothScroll();
        _offsetY = 0;
        _targetOffsetY = 0;
    }

    // ====================== Измерение ============================

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Columns <= 0) return default;
        // Владелец ещё не подключил панель: ведём себя как пустая панель.
        // availableSize может быть бесконечным (measure вне ScrollViewer) —
        // бесконечность возвращать нельзя, отдаём нулевой желаемый размер.
        if (!TryGetItems(out var items))
            return double.IsInfinity(availableSize.Height) ? default : availableSize;
        if (items.Count == 0)
        {
            // Пустой список: сохраняем вьюпорт, чтобы страница не схлопывалась.
            _viewportHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return availableSize;
        }

        _itemWidth = double.IsInfinity(availableSize.Width) ? double.NaN : availableSize.Width / Columns;
        if (double.IsNaN(_itemWidth) || _itemWidth <= 0) return default;

        // Изменение ширины ячейки (ресайз окна) — высоты строк устарели.
        if (!double.IsNaN(_lastItemWidth) && Math.Abs(_itemWidth - _lastItemWidth) > 0.5)
            _rowHeight = double.NaN;
        _lastItemWidth = _itemWidth;

        if (double.IsNaN(_rowHeight)) _rowHeight = _itemWidth * InitialRowHeightFactor;

        var rowsTotal = (items.Count + Columns - 1) / Columns;
        _extentHeight = rowsTotal * _rowHeight;

        // Бесконечная высота (measure до подключения ScrollViewer либо
        // CanContentScroll=false): прокрутка всё равно ведётся по пикселям от
        // DesiredSize. Реализуем только начальное окно по оценочному вьюпорту —
        // НЕ все строки (реализация всех карточек = прежний фриз на больших
        // библиотеках), а высоту отдаём полную, чтобы скроллбар был честным.
        if (double.IsInfinity(availableSize.Height))
        {
            if (_viewportHeight <= 0) _viewportHeight = InitialEstimatedViewport;
            MeasureWindow(new Size(availableSize.Width, _viewportHeight));
            _scrollOwner?.InvalidateScrollInfo();
            return new Size(availableSize.Width, _extentHeight);
        }

        _viewportHeight = availableSize.Height;
        MeasureWindow(availableSize);

        // Высота строки по факту измерения окна; если оценка врала — пересчитать окно
        // ещё раз с точной высотой. Смещение при коррекции МАСШТАБИРУЕТСЯ под новую
        // высоту: без этого та же пиксельная позиция после смены _rowHeight указывала
        // на другой ряд, и при скролле контент «рывком» уезжал (дёргалось от того,
        // что свежереализованный контейнер мерился до применения квадратной высоты
        // обложки). Порог 2px — гасит болтанку от шума измерений.
        var actual = ActualRowHeight();
        if (actual > 0 && Math.Abs(actual - _rowHeight) > 2)
        {
            var scale = actual / _rowHeight;
            var maxOffset = Math.Max(0, rowsTotal * actual - _viewportHeight);
            _offsetY = Math.Clamp(_offsetY * scale, 0, maxOffset);
            _targetOffsetY = Math.Clamp(_targetOffsetY * scale, 0, maxOffset);
            _rowHeight = actual;
            _extentHeight = rowsTotal * _rowHeight;
            MeasureWindow(availableSize);
        }

        _scrollOwner?.InvalidateScrollInfo();
        return availableSize;
    }

    /// <summary>Реализация контейнеров окна видимости [firstBuffered..lastBuffered] ± буфер;
    /// остальные — де-реализуются (ленивая загрузка обложек).</summary>
    private void MeasureWindow(Size availableSize)
    {
        if (!TryGetItems(out var items)) return;
        var rowsTotal = (items.Count + Columns - 1) / Columns;
        var firstVisibleRow = (int)(_offsetY / _rowHeight);
        var visibleRows = (int)Math.Ceiling(_viewportHeight / _rowHeight);
        var firstBuffered = Math.Max(0, firstVisibleRow - BufferRows);
        var lastBuffered = Math.Min(rowsTotal - 1, firstVisibleRow + visibleRows + BufferRows);

        var firstIndex = firstBuffered * Columns;
        var lastIndex = Math.Min(items.Count - 1, (lastBuffered + 1) * Columns - 1);

        var classGenerator = PanelGenerator;
        if (classGenerator == null) return;
        var iface = (IItemContainerGenerator)classGenerator;

        // Смена ширины ячейки (ресайз окна) — все дети мерились старой шириной,
        // перемериваем принудительно; в обычных проходах (в т.ч. каждый тик
        // плавного скролла) перемериваются только новые/инвалидные дети.
        var remeasureAll = double.IsNaN(_measuredChildWidth)
                           || Math.Abs(_measuredChildWidth - _itemWidth) > 0.5;

        // 1) Де-реализация вышедших из окна (и сирот после изменений коллекции).
        //    VirtualizingPanel не имеет одиночного RemoveInternalChild — используем
        //    RemoveInternalChildRange(i, 1) по индексу ребёнка во внутренней коллекции.
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            var dataIndex = classGenerator.IndexFromContainer(child);
            if (dataIndex >= firstIndex && dataIndex <= lastIndex) continue;

            var pos = iface.GeneratorPositionFromIndex(dataIndex);
            if (pos.Index >= 0 && pos.Offset == 0)
                iface.Remove(pos, 1);
            RemoveInternalChildRange(i, 1);
        }

        // 2) Реализация окна (контейнеры уже в окне — переиспользуем).
        //    Классический паттерн VSP: StartAt(позиция первого индекса,
        //    allowStartAtRealizedItems) + GenerateNext на каждый индекс окна.
        using (iface.StartAt(iface.GeneratorPositionFromIndex(firstIndex),
                             GeneratorDirection.Forward, true))
        {
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                var child = (UIElement)iface.GenerateNext(out var isNewlyRealized)!;
                if (isNewlyRealized)
                {
                    AddInternalChild(child);
                    iface.PrepareItemContainer(child);
                }

                if (isNewlyRealized || remeasureAll || !child.IsMeasureValid)
                    child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            }
        }

        _measuredChildWidth = _itemWidth;
    }

    /// <summary>Фактическая высота строки: максимум желаемой высоты реализованных детей
    /// (карточки одной строки одинаковой высоты по образцу UniformGrid).</summary>
    private double ActualRowHeight()
    {
        var max = 0.0;
        foreach (UIElement child in Children)
            max = Math.Max(max, child.DesiredSize.Height);
        return max;
    }

    // ====================== Раскладка ============================

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Columns <= 0 || double.IsNaN(_rowHeight) || _rowHeight <= 0) return finalSize;

        foreach (UIElement child in Children)
        {
            var index = PanelGenerator?.IndexFromContainer(child) ?? -1;
            if (index < 0) continue;

            var row = index / Columns;
            var column = index % Columns;
            child.Arrange(new Rect(column * _itemWidth, row * _rowHeight - _offsetY,
                                   _itemWidth, _rowHeight));
        }
        return finalSize;
    }

    // ==================== Изменения коллекции ====================

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            // Генератор уже сброшен ItemsControl-ом — выкидываем детей, скролл в начало.
            RemoveInternalChildRange(0, InternalChildren.Count);
            _rowHeight = double.NaN;
            ResetScrollOffset();
        }
        // Add/Remove/Move/Replace: индексы контейнеров пересчитывает генератор,
        // сироты вычищаются в ближайшем MeasureWindow (IndexFromContainer → -1).

        _scrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private static void OnGeometryInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VirtualizingWrapPanel)d).InvalidateMeasure();
}
