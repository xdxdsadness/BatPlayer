using System;
using System.Windows;
using System.Windows.Controls;

namespace BatPlayer.Controls;

/// <summary>
/// Border-квадрат: высота всегда равна ширине в measure-проходе. Замена связки
/// Height="{Binding ActualWidth, RelativeSource={RelativeSource Self}}" на обложках
/// карточек: тот биндинг давал КОРРЕКТНЫЙ квадрат только после arrange — карточка
/// мерилась дважды (второй лишний проход компоновки на каждую заново реализованную
/// виртуализирующей панелью карточку; при скролле новых карточок десятки в секунду).
/// Здесь квадрат известен уже в measure: один проход, без биндингов.
/// Бесконечная ширина (measure вне слота панели) — обычное поведение Border.
/// </summary>
public class SquareBorder : Border
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsNaN(constraint.Width) || double.IsInfinity(constraint.Width))
            return base.MeasureOverride(constraint);

        var width = constraint.Width;
        // Хром (рамка+паддинг) съедает ширину: ребёнку отдаём внутренний квадрат.
        var horizontal = BorderThickness.Left + BorderThickness.Right + Padding.Left + Padding.Right;
        var vertical = BorderThickness.Top + BorderThickness.Bottom + Padding.Top + Padding.Bottom;
        base.MeasureOverride(new Size(Math.Max(0, width - horizontal), Math.Max(0, width - vertical)));
        return new Size(width, width);
    }
}
