using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatPlayer.Controls;

/// <summary>
/// Тонкий слайдер с возможностью настройки толщины трека и размера ползунка.
/// Используется для прогресс-бара трека и регулятора громкости.
/// </summary>
public sealed class CustomSlider : Slider
{
    static CustomSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CustomSlider),
            new FrameworkPropertyMetadata(typeof(CustomSlider)));
        // Клик по дорожке ставит бегунок ровно в точку клика штатным механизмом
        // Slider'а (надёжный внутренний маппинг координат).
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
