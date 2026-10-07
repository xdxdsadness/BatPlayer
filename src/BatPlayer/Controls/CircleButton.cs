using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Round icon button. Used in the title bar and transport controls.
/// Transparent by default, with a subtle background on hover.
/// </summary>
public sealed class CircleButton : Button
{
    static CircleButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CircleButton),
            new FrameworkPropertyMetadata(typeof(CircleButton)));
    }

    public static readonly DependencyProperty IconGeometryProperty =
        DependencyProperty.Register(nameof(IconGeometry), typeof(Geometry), typeof(CircleButton),
            new PropertyMetadata(null));

    public Geometry? IconGeometry
    {
        get => (Geometry?)GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(CircleButton),
            new PropertyMetadata(18.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}
