using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Круглая кнопка-иконка. Используется в title bar и transport controls.
/// По умолчанию прозрачная, на hover — лёгкий фон.
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
