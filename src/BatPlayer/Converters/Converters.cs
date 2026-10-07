using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using BatPlayer.Models;

namespace BatPlayer.Converters;

/// <summary>
/// True when the platform card (SoundCloud/VK/Yandex Music) is the player's current
/// track and it is playing. Values: [0] card platform id (ScId/VkId/YmId, string),
/// [1] ScId of the player's current track, [2] IsPlaying, [3] current track's Source;
/// parameter is the expected source ("soundcloud"/"vk"/"yandex"): platform ids are
/// numeric and can coincide, so comparison happens within one platform only.
/// </summary>
public sealed class PlatformCardPlayingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: 4 }) return false;
        if (values[2] is not bool isPlaying || !isPlaying) return false;
        if (!string.Equals(values[3] as string, parameter as string, StringComparison.OrdinalIgnoreCase))
            return false;
        var cardId = values[0] as string;
        var currentId = values[1] as string;
        return !string.IsNullOrEmpty(cardId)
               && string.Equals(cardId, currentId, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the card's track is the player's current track and it is playing.
/// Values: [0] card Track, [1] player CurrentTrack, [2] IsPlaying.
/// Compares via Track.IsSameTrackAs: platform runtime cards have negative Ids that
/// coincide across lists (Home/Favorites/platform pages), so source + platform id
/// (ScId) are compared instead of Id.
/// </summary>
public sealed class CurrentTrackPlayingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: 3 }) return false;
        if (values[2] is not bool isPlaying || !isPlaying) return false;
        if (values[0] is not Track card || values[1] is not Track current) return false;
        return card.IsSameTrackAs(current);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when THIS playlist's queue is playing — the card shows Pause instead of Play
/// and keeps the hover overlay like a playing track card. Values: [0] card playlist Id,
/// [1] player CurrentPlaylistId, [2] IsPlaying.
/// </summary>
public sealed class PlaylistCardPlayingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: 3 }) return false;
        if (values[2] is not bool isPlaying || !isPlaying) return false;
        var cardId = values[0] as long? ?? (values[0] is long l ? l : null);
        var currentId = values[1] as long? ?? (values[1] is long c ? c : null);
        return cardId.HasValue && currentId.HasValue && cardId == currentId;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>string: Visible when null/empty (placeholder), Collapsed otherwise.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var i = value is int n ? n : 0;
        return i > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseIntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var i = value is int n ? n : 0;
        return i > 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is bool b && b);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is bool b && b);
}

public sealed class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }
        return "00:00";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>String → Visible when it equals parameter (case-insensitive); otherwise Collapsed.
/// Platform badges: track Source ("soundcloud"/"vk"/"yandex") vs the expected one.</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>false → Visible, true → Collapsed (inverse of BoolToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class VolumeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Volume arrives as double (0..100); integer types are accepted just in case.
        var v = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => 0d
        };
        if (v <= 0) return "Mute";
        if (v < 33) return "Low";
        if (v < 66) return "Medium";
        return "High";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Cover image path -> BitmapImage with a cache and reduced decode resolution.
/// Previously every call decoded the FULL-SIZE cover from disk: library cards, profiles,
/// artists — hundreds of 1-3MB images => hundreds of MB of RAM and scroll freezes.
/// Now:
/// - static cache keyed by file path (the same cover is shared across all views);
/// - DecodePixelWidth = 400: grid covers are <= ~450px, mini covers 48-64px even less;
/// - CacheOption.OnLoad + Freeze: the file is not held open, BitmapImage is thread-safe;
/// - ~300 entry limit: on overflow the cache is cleared — OnLoad allows a safe re-read.
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    private const int DecodePixelWidth = 400;
    private const int MaxCacheEntries = 300;

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return DependencyProperty.UnsetValue;
        if (Cache.TryGetValue(path, out var cached))
            return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = DecodePixelWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            if (Cache.Count >= MaxCacheEntries)
                Cache.Clear();
            Cache[path] = bmp;
            return bmp;
        }
        catch { return DependencyProperty.UnsetValue; }
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Snaps the list height to a whole number of rows so the bottom row is not clipped.
/// value is the available height (double), parameter is "rowHeight,headerHeight" (e.g. "41,32").
/// </summary>
public sealed class SnapToRowsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double h || double.IsNaN(h) || h <= 0)
            return double.NaN;

        // Parameters: row height, header height [, pager reserve]
        var parts = (parameter as string)?.Split(',') ?? new[] { "41", "32" };
        var row = double.TryParse(parts[0], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 41;
        var header = double.TryParse(parts.Length > 1 ? parts[1] : "32", System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var hd) ? hd : 32;
        var extra = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var ex) ? ex : 0;

        var listHeight = h - header - extra;
        if (listHeight < row) return header;
        return Math.Floor(listHeight / row) * row + header;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FileSizeToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes) return Helpers.FileHelpers.FormatFileSize(bytes);
        return "—";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlayStateToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "Pause" : "Play";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RepeatModeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.RepeatMode r)
        {
            return r switch
            {
                Models.RepeatMode.RepeatOne => "RepeatOne",
                Models.RepeatMode.RepeatAll => "RepeatAll",
                _ => "RepeatOff"
            };
        }
        return "RepeatOff";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Two-way binding of a VM enum property (e.g. StatsViewModel.Period) to the IsChecked
/// of a RadioButton group: parameter is the value name (CommandParameter).
/// Convert: true when the property value equals parameter.
/// ConvertBack: unchecking (false) must not overwrite the VM — Binding.DoNothing;
/// checking parses parameter into the enum type of the current value.
/// </summary>
public sealed class EnumEqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (enumType.IsEnum)
            return Enum.Parse(enumType, parameter.ToString()!, ignoreCase: true);
        return parameter.ToString()!;
    }
}

/// <summary>
/// bool (switch IsChecked) → thumb X offset in pixels: off = 0, on = 20
/// (Track 42 - Thumb 16 - margins 3+3). The binding gives the correct position right at
/// load (including checked-by-default), while the template triggers animate X on top of
/// the binding with FillBehavior=Stop — when done, the value returns to the binding of
/// the new state.
/// </summary>
public sealed class BoolToSwitchOffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 20d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
