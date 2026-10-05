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
/// Позволяет зажать ЛКМ в любой точке полоски слайдера и тащить ползунок,
/// не целясь точно в бегунок.
/// Поведение:
/// - клик в любой точке сразу прыгает ползунком в точку клика и (если задан
///   SeekCommand) немедленно вызывает его — мгновенный отклик, без ожидания
///   отпускания;
/// - при перетаскивании Value обновляется локально (без seek на каждый пиксель),
///   финальный SeekCommand вызывается по отпусканию;
/// - OneWay-биндинг Value (позиция воспроизведения) на время перетаскивания
///   отключается и восстанавливается по отпусканию / потере capture;
/// - TwoWay-биндинг (громкость) НЕ отключается: локальные присваивания Value
///   сами проталкиваются в источник (привязка при этом не рвётся), поэтому
///   громкость меняется непрерывно уже в процессе движения.
/// </summary>
public static class SliderDragBehavior
{
    private sealed class DragState
    {
        public bool Dragging;
        public Binding? SavedBinding;  // биндинг Value, снятый при MouseDown (любой режим)
        public bool BindingDetached;   // снимали ли мы биндинг фактически
        public long LastSeekTick;      // троттлинг непрерывного seek при перетаскивании
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
            // Bubbled-подписка с handledEventsToo: штатный Slider сам ставит бегунок
            // в точку клика (IsMoveToPointEnabled) и помечает событие обработанным —
            // нам нужно выполниться ПОСЛЕ него, когда Value уже равен точке клика.
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

        // Захватываем ЛЮБОЙ активный биндинг Value. Снимаем только не-TwoWay
        // (позиция воспроизведения): иначе он будет бороться с перетаскиванием.
        // TwoWay (громкость) оставляем на месте — через него Value доходит
        // до источника непрерывно, см. шапку класса.
        st.SavedBinding = null;
        st.BindingDetached = false;
        if (slider.GetBindingExpression(RangeBase.ValueProperty) is { } bex)
        {
            st.SavedBinding = bex.ParentBinding;
            if (bex.ParentBinding.Mode != BindingMode.TwoWay)
            {
                st.BindingDetached = true;
                var keep = slider.Value; // позиция до отвязки
                slider.ClearValue(RangeBase.ValueProperty);
                // ClearValue сбрасывает Value в дефолт — возвращаем, чтобы бегунок
                // не прыгал в ноль при клике прямо по нему (нативный move-to-point
                // сработает только для кликов по дорожке).
                slider.Value = keep;
            }
        }

        st.Dragging = true;
        slider.CaptureMouse();
        // e.Handled НЕ ставим: штатный Slider.OnMouseLeftButtonDown (move-to-point,
        // IsMoveToPointEnabled) должен отработать и поставить бегунок в точку клика —
        // наш seek выполнится в OnMouseDownBubbled уже по новому значению.
    }

    // Выполняется ПОСЛЕ штатного Slider.OnMouseLeftButtonDown: бегунок уже
    // переместился в точку клика (IsMoveToPointEnabled) — теперь seek по факту.
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

        // Непрерывный seek при зажатой кнопке (как в Spotify/YouTube): пока тянем,
        // позиция трека следует за курсором с троттлингом ~150 мс — финальный
        // seek всё равно выполняется при отпускании.
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

        // Финальный seek по фактическому положению после перетаскивания.
        // Порядок важен: сначала seek (VM оптимистично обновит Position),
        // затем восстановление биндинга — тот подтянет уже новую позицию.
        GetSeekCommand(slider)?.Execute(ValueToPercent(slider));

        RestoreBinding(slider, st);
        e.Handled = true;
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        var st = State(slider);
        if (!st.Dragging) return;

        // Захват мог перехватить СОБСТВЕННЫЙ Thumb слайдера (пользователь нажал
        // прямо на бегунок: наш Preview-обработчик отработал раньше и захватил
        // мышь слайдером, затем нативный Thumb забрал capture себе). Это часть
        // нормального перетаскивания: drag продолжается (события всё равно
        // проходят через Preview-обработчики слайдера на пути к Thumb), а вот
        // преждевременное завершение здесь ломало таймлайн — биндинг
        // возвращался, тики позиции отбрасывали бегунок назад каждые 250 мс,
        // и финальный seek по отпусканию не выполнялся вовсе.
        if (Mouse.Captured is Thumb) return;

        // Потеря capture (Alt+Tab, системное меню и т.п.): завершаем drag
        // и возвращаем биндинг на место.
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
        // Позиция мыши — строго из ЖИВОГО устройства (Mouse.GetPosition), а не из
        // аргументов события: у PreviewMouseButtonDown-аргументов позиция
        // относительно PART_Track периодически приходила нулевой, из-за чего клик
        // по таймлайну вычислял 0% и трек «начинался сначала».
        if (slider.Template?.FindName("PART_Track", slider) is Track track)
        {
            var value = track.ValueFromPoint(Mouse.GetPosition(track));
            if (!double.IsNaN(value))
            {
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
                return;
            }
        }

        // Fallback, если шаблон ещё не применён / Track не найден: ручной расчёт.
        var p = Mouse.GetPosition(slider);
        bool vertical = slider.Orientation == Orientation.Vertical;
        double length = vertical ? slider.ActualHeight : slider.ActualWidth;
        double pos = vertical ? p.Y : p.X;

        double inset = 7; // половина ширины бегунка
        if (slider.Template?.FindName("PART_Track", slider) is Track tr &&
            tr.Thumb is { } thumb)
            inset = vertical ? thumb.ActualHeight / 2 : thumb.ActualWidth / 2;

        double inner = Math.Max(1, length - inset * 2);
        double t = Math.Clamp((pos - inset) / inner, 0, 1);
        if (vertical) t = 1 - t;

        slider.Value = slider.Minimum + t * (slider.Maximum - slider.Minimum);
    }
}
