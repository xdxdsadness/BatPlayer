using System;
using System.Windows;
using System.Windows.Controls;

namespace BatPlayer.Controls;

/// <summary>
/// Square Border: height always equals width in the measure pass. Replaces the
/// Height="{Binding ActualWidth, RelativeSource={RelativeSource Self}}" binding on card
/// covers: that binding produced a CORRECT square only after arrange — the card was
/// measured twice (an extra layout pass for every card re-realized by the virtualizing
/// panel; dozens of new cards per second while scrolling). Here the square is known
/// already in measure: one pass, no bindings. Infinite width (measure outside a panel
/// slot) falls back to regular Border behavior.
/// </summary>
public class SquareBorder : Border
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsNaN(constraint.Width) || double.IsInfinity(constraint.Width))
            return base.MeasureOverride(constraint);

        var width = constraint.Width;
        // Chrome (border+padding) eats width: give the child the inner square.
        var horizontal = BorderThickness.Left + BorderThickness.Right + Padding.Left + Padding.Right;
        var vertical = BorderThickness.Top + BorderThickness.Bottom + Padding.Top + Padding.Bottom;
        base.MeasureOverride(new Size(Math.Max(0, width - horizontal), Math.Max(0, width - vertical)));
        return new Size(width, width);
    }
}
