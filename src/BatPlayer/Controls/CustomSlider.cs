using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Slim slider with configurable track thickness and thumb size.
/// Used for the track progress bar and the volume control.
/// </summary>
public sealed class CustomSlider : Slider
{
    static CustomSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CustomSlider),
            new FrameworkPropertyMetadata(typeof(CustomSlider)));
        // Clicking the track moves the thumb exactly to the click point via the stock
        // Slider mechanism (reliable internal coordinate mapping).
        IsMoveToPointEnabledProperty.OverrideMetadata(typeof(CustomSlider),
            new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty TrackHeightProperty =
        DependencyProperty.Register(nameof(TrackHeight), typeof(double), typeof(CustomSlider),
            new PropertyMetadata(4.0));

    public double TrackHeight
    {
        get => (double)GetValue(TrackHeightProperty);
        set => SetValue(TrackHeightProperty, value);
    }

    public static readonly DependencyProperty ThumbSizeProperty =
        DependencyProperty.Register(nameof(ThumbSize), typeof(double), typeof(CustomSlider),
            new PropertyMetadata(14.0));

    public double ThumbSize
    {
        get => (double)GetValue(ThumbSizeProperty);
        set => SetValue(ThumbSizeProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(CustomSlider),
            new PropertyMetadata(2.0));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
