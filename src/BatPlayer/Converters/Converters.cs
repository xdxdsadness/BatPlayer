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
/// true, если платформенная карточка (SoundCloud/VK/Яндекс Музыка) — текущий трек плеера
/// и он сейчас играет. Значения: [0] id карточки на платформе (ScId/VkId/YmId, строка),
/// [1] ScId текущего трека плеера, [2] IsPlaying, [3] Source текущего трека;
/// parameter — ожидаемый источник ("soundcloud"/"vk"/"yandex"): id разных платформ
/// числовые и могут совпадать, сравниваем только внутри одной платформы.
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
/// true, если трек карточки — текущий трек плеера и он сейчас играет.
/// Значения: [0] Track карточки, [1] CurrentTrack плеера, [2] IsPlaying.
/// Сравнение — через Track.IsSameTrackAs: у runtime-карточек платформ Id
/// отрицательные и совпадают между разными списками (Home/Favorites/страницы
/// платформ), поэтому сверяем источник + id на платформе (ScId), а не Id.
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
/// true, если играет очередь ЭТОГО плейлиста — карточка показывает Pause вместо Play
/// и держит hover-оверлей, как карточка играющего трека. Значения: [0] Id плейлиста
/// карточки, [1] CurrentPlaylistId плеера, [2] IsPlaying.
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

/// <summary>
/// Загрузка обложки по http(s)-ссылке (обложки SoundCloud). BitmapImage качает файл
/// асинхронно сам, если создан на UI-потоке и не заморожен (Freeze() убил бы докачку).
/// Кэш по URL: без него прокрутка списка перекачивала бы каждую обложку заново.
/// </summary>
public sealed class UrlToImageConverter : IValueConverter
{
    private const int MaxCacheEntries = 600;

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
        new(StringComparer.Ordinal);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrEmpty(url)) return DependencyProperty.UnsetValue;
        if (Cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            // Без DecodePixelWidth: WPF декодирует по месту (обложки ~500px, список небольшой).
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.None; // async download; OnLoad заблокировал бы UI
            bmp.EndInit();
            if (Cache.Count >= MaxCacheEntries) Cache.Clear();
            Cache[url] = bmp;
            return bmp;
        }
        catch { return DependencyProperty.UnsetValue; }
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Строка → Visible, если совпадает с parameter (без учёта регистра); иначе Collapsed.
/// Бейджи платформы: Source трека ("soundcloud"/"vk"/"yandex") против ожидаемого.</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>false → Visible, true → Collapsed (пара к BoolToVisibilityConverter).</summary>
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
        // Громкость пришла double (0..100); принимаем на всякий случай и целые типы.
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
/// Путь к картинке обложки -> BitmapImage с кэшем и пониженным разрешением декодирования.
/// Раньше каждый вызов декодировал ПОЛНОРАЗМЕРНУЮ обложку с диска: карточки библиотеки,
/// профили, артисты — сотни картинок по 1-3МБ => сотни МБ RAM и фризы при скролле.
/// Теперь:
/// - статический кэш по пути файла (одни и те же обложки шарятся между всеми view);
/// - DecodePixelWidth = 400: обложки в сетке ≤ ~450px, для 48-64px мини-обложек тем более;
/// - CacheOption.OnLoad + Freeze: файл не держится открытым, BitmapImage потокобезопасен;
/// - лимит ~300 записей: при переполнении кэш сбрасывается целиком — OnLoad позволяет
///   безопасно перечитать с диска.
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
/// Подгоняет высоту списка под целое число строк, чтобы нижняя строка не обрезалась.
/// value — доступная высота (double), parameter — "rowHeight,headerHeight" (напр. "41,32").
/// </summary>
public sealed class SnapToRowsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double h || double.IsNaN(h) || h <= 0)
            return double.NaN;

        // Параметры: высота строки, высота заголовка [, резерв под пейджер]
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
/// Двусторонний биндинг enum-свойства VM (например, StatsViewModel.Period) на
/// IsChecked группы RadioButton: parameter — имя значения (CommandParameter).
/// Convert: true, если значение свойства совпадает с parameter.
/// ConvertBack: сброс галочки (false) не должен переписывать VM — Binding.DoNothing;
/// установка галочки парсит parameter в enum того же типа, что и текущее значение.
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
/// bool (IsChecked переключателя) → смещение бегунка по X в пикселях: выключено — 0,
/// включено — 20 (Track 42 − Thumb 16 − поля 3+3). Биндинг даёт верное положение
/// сразу при загрузке (в т.ч. для включённых по умолчанию), а триггеры шаблона
/// анимируют X поверх биндинга с FillBehavior=Stop — по завершении значение
/// возвращается к биндингу уже нового состояния.
/// </summary>
public sealed class BoolToSwitchOffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 20d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
