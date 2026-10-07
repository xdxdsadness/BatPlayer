using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Virtualizing grid panel: a fixed number of equal-width columns, row height —
/// by the tallest child of the window. Replaces UniformGrid in card lists (Library/
/// SoundCloud/YM/VK/Artists/Downloads/ArtistProfile): UniformGrid doesn't virtualize —
/// hundreds of cards realize all at once (memory + navigation lag), while here only the
/// rows of the visible area ± buffer are created: covers load lazily, "as you scroll".
///
/// Implementation follows the VirtualizingPanel + IScrollInfo canon: the ScrollViewer
/// (CanContentScroll) delegates scrolling to the panel; extent = rows × row height;
/// containers are re-realized for the visibility window via ItemContainerGenerator.
/// Vertical scrolling is pixel-based.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    // ========================= Settings =========================

    /// <summary>Number of grid columns (like UniformGrid's Columns).</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(VirtualizingWrapPanel),
        new PropertyMetadata(4, OnGeometryInvalidated));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Rows above/below the visible area kept realized (scroll margin).
    /// 2, not 1: a row is realized AHEAD of entering the frame — SmoothScroll's long
    /// smooth glide doesn't stumble on creating a heavy card template on the fly.</summary>
    private const int BufferRows = 2;

    private const double ScrollLineDelta = 16.0;
    private const double WheelLinesPerTick = 3.0;

    // ===== Smooth (animated) scrolling =====
    // Wheel/line scrolling doesn't jump instantly: the target is written to _targetOffsetY
    // and _offsetY catches up exponentially in a ~60fps timer — content "glides" instead
    // of jerking. Direct offset assignments (scrollbar, MakeVisible, collection resets)
    // bypass the animation: both the offset and the target are set immediately.
    /// <summary>Fraction of the distance to the target per tick: 0.35 closes the gap in
    /// ~4-5 frames (~70ms); the motion stays fast but with visible inertia.</summary>
    private const double SmoothScrollLerp = 0.35;
    /// <summary>Closer than this (px) to the target — snap instantly and stop the timer.</summary>
    private const double SmoothScrollSnap = 0.5;
    /// <summary>Animation tick period ≈ one frame at 60 Hz.</summary>
    private const int SmoothScrollIntervalMs = 16;

    /// <summary>Row height estimate before the first measure: a card with a square cover
    /// ≈ cell width + captions. After the first measure it's refined to the actual height.</summary>
    private const double InitialRowHeightFactor = 1.2;

    /// <summary>Viewport estimate before the ScrollViewer is attached (px):
    /// enough for the first rows, doesn't realize the whole list.</summary>
    private const double InitialEstimatedViewport = 720.0;

    // ======================= State ===========================

    private ScrollViewer? _scrollOwner;

    private double _offsetY;                 // vertical scroll in pixels
    private double _targetOffsetY;           // smooth scroll target (wheel/line)
    private System.Windows.Threading.DispatcherTimer? _smoothScrollTimer;
    private double _viewportHeight;
    private double _extentHeight;
    private double _itemWidth;               // cell width = viewport / Columns
    private double _lastItemWidth = double.NaN;
    private double _rowHeight = double.NaN;  // row height; NaN — re-estimate
    // The width the cards were last measured at: the scroll timer fires InvalidateMeasure
    // ~60 times/sec, and without this field every tick RE-MEASURED all realized cards
    // (dozens of heavy templates per frame — the main source of jank when scrolling
    // large libraries). Children with valid measure and the same width are skipped; any
    // change to a card's content resets its IsMeasureValid itself → it re-measures normally.
    private double _measuredChildWidth = double.NaN;

    /// <summary>
    /// Panel owner: the nearest ItemsControl up the visual tree (ListView is an ancestor:
    /// ListView → Border → ScrollViewer → … → ItemsPresenter → panel). IMPORTANT:
    /// ItemsControl.ItemsControlFromItemContainer for the host panel itself always returns
    /// null (verified via an STA probe even for the stock VirtualizingStackPanel) — the
    /// panel's generator is attached only through ItemsPresenter, so the ItemsControl is
    /// more reliably found by walking the tree.
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
    /// The owner's item collection. Until the panel is inserted into the ItemsControl's
    /// tree (the first measure can happen earlier), behave like an empty panel — instead
    /// of crashing the whole layout.
    /// IMPORTANT: WPF delivers generator notifications only to stock panels (STA probe:
    /// our panel's VirtualizingPanel.ItemContainerGenerator is null forever), so Items
    /// changes are listened to directly — ItemCollection implements
    /// INotifyCollectionChanged. Without this, Clear/Add of cards on navigation
    /// wouldn't repaint the panel.
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

    /// <summary>Items change (Clear/Add on navigation and syncs): reset the window and re-layout.</summary>
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
    /// The generator bound to this panel. Priority goes to the stock
    /// VirtualizingPanel.ItemContainerGenerator (attached by ItemsPresenter for stock
    /// panels); if it didn't arrive, create a panel view ourselves via the public
    /// IItemContainerGenerator.GetItemContainerGeneratorForPanel.
    /// IMPORTANT: the owner's "raw" ItemContainerGenerator must not be used — it has its
    /// own generation session, and GenerateNext with it throws
    /// ("Must call GenerateNext while content generation is in progress").
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

    /// <summary>Nearest ItemsControl above the element (a card container).</summary>
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

    public void SetHorizontalOffset(double offset) { /* no horizontal scrolling */ }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extentHeight - _viewportHeight);
        var clamped = Math.Max(0, Math.Min(offset, max));
        // Direct assignment (scrollbar, MakeVisible, reset on collection change):
        // no animation — stop the timer and set the offset and target synchronously.
        StopSmoothScroll();
        if (Math.Abs(clamped - _offsetY) < 0.5 && Math.Abs(clamped - _targetOffsetY) < 0.5) return;
        _offsetY = clamped;
        _targetOffsetY = clamped;
        InvalidateMeasure();
        _scrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Set the smooth scroll target (wheel/line): clamp to the extent; the
    /// animation timer is started lazily and reused (the panel lives long — one timer
    /// for the panel's lifetime, not one per scroll).</summary>
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

    /// <summary>Animation tick: _offsetY catches up to the target exponentially; below
    /// the threshold distance — snap and stop. Clamping the target to the extent covers
    /// the case where the list shrank mid-animation.</summary>
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

    /// <summary>Reset scroll to the top without animation (collection resets, list rebuilds).</summary>
    private void ResetScrollOffset()
    {
        StopSmoothScroll();
        _offsetY = 0;
        _targetOffsetY = 0;
    }

    // ====================== Measuring ============================

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Columns <= 0) return default;
        // The owner hasn't attached the panel yet: behave like an empty panel.
        // availableSize can be infinite (measure outside a ScrollViewer) —
        // infinity must not be returned; give a zero desired size.
        if (!TryGetItems(out var items))
            return double.IsInfinity(availableSize.Height) ? default : availableSize;
        if (items.Count == 0)
        {
            // Empty list: keep the viewport so the page doesn't collapse.
            _viewportHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return availableSize;
        }

        _itemWidth = double.IsInfinity(availableSize.Width) ? double.NaN : availableSize.Width / Columns;
        if (double.IsNaN(_itemWidth) || _itemWidth <= 0) return default;

        // Cell width change (window resize) — row heights are stale.
        if (!double.IsNaN(_lastItemWidth) && Math.Abs(_itemWidth - _lastItemWidth) > 0.5)
            _rowHeight = double.NaN;
        _lastItemWidth = _itemWidth;

        if (double.IsNaN(_rowHeight)) _rowHeight = _itemWidth * InitialRowHeightFactor;

        var rowsTotal = (items.Count + Columns - 1) / Columns;
        _extentHeight = rowsTotal * _rowHeight;

        // Infinite height (measure before the ScrollViewer is attached, or
        // CanContentScroll=false): scrolling is still pixel-based from DesiredSize.
        // Realize only the initial window by the estimated viewport — NOT all rows
        // (realizing every card = the old freeze on large libraries), and report the
        // full height so the scrollbar is honest.
        if (double.IsInfinity(availableSize.Height))
        {
            if (_viewportHeight <= 0) _viewportHeight = InitialEstimatedViewport;
            MeasureWindow(new Size(availableSize.Width, _viewportHeight));
            _scrollOwner?.InvalidateScrollInfo();
            return new Size(availableSize.Width, _extentHeight);
        }

        _viewportHeight = availableSize.Height;
        MeasureWindow(availableSize);

        // Row height from the actual window measurement; if the estimate was wrong,
        // re-measure the window once more with the exact height. The offset during the
        // correction is SCALED to the new height: without it the same pixel position
        // after _rowHeight changed pointed at a different row, and scrolling "yanked"
        // the content (a freshly realized container was measured before the square
        // cover height applied). The 2px threshold damps measurement-noise jitter.
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

    /// <summary>Realize containers of the visibility window [firstBuffered..lastBuffered]
    /// ± buffer; the rest are de-realized (lazy cover loading).</summary>
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

        // Cell width change (window resize) — all children were measured at the old
        // width, force a re-measure; in normal passes (including every smooth-scroll
        // tick) only new/invalidated children are measured.
        var remeasureAll = double.IsNaN(_measuredChildWidth)
                           || Math.Abs(_measuredChildWidth - _itemWidth) > 0.5;

        // 1) De-realize those out of the window (and orphans after collection changes).
        //    VirtualizingPanel has no single RemoveInternalChild — use
        //    RemoveInternalChildRange(i, 1) by the child's index in the internal collection.
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

        // 2) Realize the window (containers already in the window are reused).
        //    Classic VSP pattern: StartAt(position of the first index,
        //    allowStartAtRealizedItems) + GenerateNext for each window index.
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

    /// <summary>Actual row height: the max desired height of realized children
    /// (cards of one row are equal-height, UniformGrid style).</summary>
    private double ActualRowHeight()
    {
        var max = 0.0;
        foreach (UIElement child in Children)
            max = Math.Max(max, child.DesiredSize.Height);
        return max;
    }

    // ====================== Layout ============================

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

    // ==================== Collection changes ====================

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            // The generator was already reset by the ItemsControl — drop the children, scroll to the top.
            RemoveInternalChildRange(0, InternalChildren.Count);
            _rowHeight = double.NaN;
            ResetScrollOffset();
        }
        // Add/Remove/Move/Replace: the generator recomputes container indices,
        // orphans are cleaned up in the next MeasureWindow (IndexFromContainer → -1).

        _scrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private static void OnGeometryInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VirtualizingWrapPanel)d).InvalidateMeasure();
}
