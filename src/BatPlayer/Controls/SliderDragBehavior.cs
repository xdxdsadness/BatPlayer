using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Lets the user press LMB anywhere on the slider track and drag the thumb without
/// aiming exactly at it.
/// Behavior:
/// - a click anywhere jumps the thumb to the click point and (if SeekCommand is set)
///   invokes it immediately — instant response, no waiting for release;
/// - while dragging, Value updates locally (no seek per pixel); the final SeekCommand
///   runs on release;
/// - a OneWay Value binding (playback position) is detached while dragging and restored
///   on release / capture loss;
/// - a TwoWay binding (volume) is NOT detached: local Value assignments push through to
///   the source themselves (the binding stays intact), so the volume changes continuously
///   while moving.
/// </summary>
public static class SliderDragBehavior
{
    private sealed class DragState
    {
        public bool Dragging;
        public Binding? SavedBinding;  // Value binding saved at MouseDown (any mode)
        public bool BindingDetached;   // whether we actually detached the binding
        public long LastSeekTick;      // throttling continuous seek while dragging
    }

    private static readonly Dictionary<Slider, DragState> States = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SliderDragBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty SeekCommandProperty =
        DependencyProperty.RegisterAttached("SeekCommand", typeof(ICommand), typeof(SliderDragBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetSeekCommand(DependencyObject obj) => (ICommand?)obj.GetValue(SeekCommandProperty);
    public static void SetSeekCommand(DependencyObject obj, ICommand? value) => obj.SetValue(SeekCommandProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        if ((bool)e.NewValue)
        {
            States[slider] = new DragState();
            slider.PreviewMouseLeftButtonDown += OnMouseDown;
            slider.PreviewMouseMove += OnMouseMove;
            slider.PreviewMouseLeftButtonUp += OnMouseUp;
            slider.LostMouseCapture += OnLostMouseCapture;
            // Bubbled subscription with handledEventsToo: the stock Slider moves the thumb
            // to the click point itself (IsMoveToPointEnabled) and marks the event handled —
            // we need to run AFTER it, when Value already equals the click point.
            slider.AddHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnMouseDownBubbled), true);
        }
        else
        {
            States.Remove(slider);
            slider.PreviewMouseLeftButtonDown -= OnMouseDown;
            slider.PreviewMouseMove -= OnMouseMove;
            slider.PreviewMouseLeftButtonUp -= OnMouseUp;
            slider.LostMouseCapture -= OnLostMouseCapture;
            slider.RemoveHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnMouseDownBubbled));
        }
    }

    private static DragState State(Slider slider) => States.TryGetValue(slider, out var st) ? st : new DragState();

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.Maximum <= slider.Minimum) return;

        var st = State(slider);

        // Capture ANY active Value binding. Detach only non-TwoWay ones (playback
        // position): otherwise it would fight the dragging. TwoWay (volume) stays in
        // place — Value reaches the source continuously through it, see the class header.
        st.SavedBinding = null;
        st.BindingDetached = false;
        if (slider.GetBindingExpression(RangeBase.ValueProperty) is { } bex)
        {
            st.SavedBinding = bex.ParentBinding;
            if (bex.ParentBinding.Mode != BindingMode.TwoWay)
            {
                st.BindingDetached = true;
                var keep = slider.Value; // position before detaching
                slider.ClearValue(RangeBase.ValueProperty);
                // ClearValue resets Value to default — restore it so the thumb doesn't
                // jump to zero on a click right on the thumb (native move-to-point only
                // works for clicks on the track).
                slider.Value = keep;
            }
        }

        st.Dragging = true;
        slider.CaptureMouse();
        // Don't set e.Handled: the stock Slider.OnMouseLeftButtonDown (move-to-point,
        // IsMoveToPointEnabled) must run and move the thumb to the click point —
        // our seek runs in OnMouseDownBubbled on the new value.
    }

    // Runs AFTER the stock Slider.OnMouseLeftButtonDown: the thumb has already moved
    // to the click point (IsMoveToPointEnabled) — now seek on fact.
    private static void OnMouseDownBubbled(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        var st = State(slider);
        if (!st.Dragging) return;

        BatPlayer.Services.Logger.Info($"[DRAG] down-seek: pct={ValueToPercent(slider):0.000}");
        GetSeekCommand(slider)?.Execute(ValueToPercent(slider));
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        var st = State(slider);
        if (!st.Dragging)
            return;

        ApplyFromMouse(slider, e);

        // Continuous seek while the button is held (like Spotify/YouTube): while dragging,
        // the track position follows the cursor throttled to ~150ms — the final seek
        // runs on release anyway.
        var now = Environment.TickCount64;
        if (now - st.LastSeekTick < 150)
            return;
        st.LastSeekTick = now;
        GetSeekCommand(slider)?.Execute(ValueToPercent(slider));
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        var st = State(slider);
        if (!st.Dragging) return;

        BatPlayer.Services.Logger.Info($"[DRAG] up: pct={ValueToPercent(slider):0.000} val={slider.Value:0.00}");
        st.Dragging = false;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();

        // Final seek at the actual position after dragging.
        // Order matters: seek first (the VM optimistically updates Position),
        // then restore the binding — it picks up the new position.
        GetSeekCommand(slider)?.Execute(ValueToPercent(slider));

        RestoreBinding(slider, st);
        e.Handled = true;
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        var st = State(slider);
        if (!st.Dragging) return;

        // The capture may have been taken by the slider's OWN Thumb (the user pressed
        // right on the thumb: our Preview handler ran first and captured the mouse to
        // the slider, then the native Thumb took capture for itself). This is part of
        // normal dragging: the drag continues (events still pass through the slider's
        // Preview handlers on the way to the Thumb), while finishing early here broke
        // the timeline — the binding returned, position ticks threw the thumb back
        // every 250ms, and the final seek on release never ran at all.
        if (Mouse.Captured is Thumb) return;

        // Capture lost (Alt+Tab, system menu etc.): finish the drag
        // and put the binding back.
        st.Dragging = false;
        RestoreBinding(slider, st);
    }

    private static void RestoreBinding(Slider slider, DragState st)
    {
        if (!st.BindingDetached) return;
        if (st.SavedBinding != null)
            slider.SetBinding(RangeBase.ValueProperty, st.SavedBinding);
        st.SavedBinding = null;
        st.BindingDetached = false;
    }

    private static double ValueToPercent(Slider slider)
        => (slider.Value - slider.Minimum) / Math.Max(1e-9, slider.Maximum - slider.Minimum);

    private static void ApplyFromMouse(Slider slider, MouseEventArgs e)
    {
        // Mouse position strictly from the LIVE device (Mouse.GetPosition), not from the
        // event args: the PreviewMouseButtonDown args' position relative to PART_Track
        // periodically arrived as zero, so a timeline click computed 0% and the track
        // "started over".
        if (slider.Template?.FindName("PART_Track", slider) is Track track)
        {
            var value = track.ValueFromPoint(Mouse.GetPosition(track));
            if (!double.IsNaN(value))
            {
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
                return;
            }
        }

        // Fallback if the template isn't applied yet / Track not found: manual calculation.
        var p = Mouse.GetPosition(slider);
        bool vertical = slider.Orientation == Orientation.Vertical;
        double length = vertical ? slider.ActualHeight : slider.ActualWidth;
        double pos = vertical ? p.Y : p.X;

        double inset = 7; // half the thumb width
        if (slider.Template?.FindName("PART_Track", slider) is Track tr &&
            tr.Thumb is { } thumb)
            inset = vertical ? thumb.ActualHeight / 2 : thumb.ActualWidth / 2;

        double inner = Math.Max(1, length - inset * 2);
        double t = Math.Clamp((pos - inset) / inner, 0, 1);
        if (vertical) t = 1 - t;

        slider.Value = slider.Minimum + t * (slider.Maximum - slider.Minimum);
    }
}
