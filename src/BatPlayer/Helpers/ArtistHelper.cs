using BatPlayer.Localization;
using System.Collections.Generic;
using System.Linq;

namespace BatPlayer.Helpers;

/// <summary>
/// Нормализация имён исполнителей. Пустые и legacy-значения из старых записей БД
/// склеиваются в один ключ "", чтобы все «неизвестные» артисты группировались вместе
/// (страница исполнителей, профиль, клики по имени).
/// </summary>
public static partial class ArtistHelper
{
    // Значения, которые старые версии приложения писали в БД вместо пустых тегов.
    private static readonly string[] LegacyUnknownArtists =
    {
        "Неизвестный исполнитель",
        "Неизвестный артист",
        "Unknown Artist",
    };

    /// <summary>
    /// Ключ группировки: "" для неизвестных исполнителей, иначе имя в нижнем регистре.
    /// Регистронезависимость склеивает "Kai Angel" и "kai angel" в одного артиста,
    /// включая треки с разных платформ.
    /// </summary>
    public static string Key(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;

        var trimmed = artist.Trim();
        foreach (var legacy in LegacyUnknownArtists)
            if (string.Equals(trimmed, legacy, StringComparison.Ordinal))
                return string.Empty;

        return trimmed.ToLowerInvariant();
    }

    /// <summary>Отрезает фитовую часть имени: "X feat. B" → "X", "X (ft. B)" → "X",
    /// "X featuring B" → "X". Нужно для канонизации семейства исполнителя — трек
    /// "X feat. B" и трек "X" принадлежат одному артисту X.</summary>
    public static string StripFeatures(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        var s = FeatParenRegex().Replace(artist, " ");
        return FeatTailRegex().Replace(s, " ").Trim();
    }

    // "(feat. B)", "(with D)", "(ft. B & C)" — скобочный фит целиком (SC-стиль "with" тоже).
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\((?:feat\.?|ft\.?|featuring|with)\s+[^)]*\)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FeatParenRegex();

    // "feat. B", "ft B", "featuring B & C" — хвост после фита.
    [System.Text.RegularExpressions.GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FeatTailRegex();

    /// <summary>
    /// Разбивка строки исполнителей на отдельных артистов: запятая и амперсанд
    /// разделяют соавторов ("August, TikoTheCEO" даёт прослушку
    /// ОБОИМ артистам, а регистронезависимый ключ склеивает "Kai Angel"/"kai angel"
    /// и треки разных платформ.
    /// Возвращает максимум 6 имён, дубликаты (без учёта регистра) убираются.
    /// </summary>
    public static List<string> Split(string? artist)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(artist)) return result;

        foreach (var raw in artist.Split([',', '&'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var key = Key(name);
            if (key.Length == 0) continue;
            if (result.Any(r => Key(r) == key)) continue;
            result.Add(name);
            if (result.Count >= 6) break;
        }
        return result;
    }

    /// <summary>Имя для отображения: пустой ключ заменяется локализованной строкой.</summary>
    public static string DisplayName(string key)
        => string.IsNullOrEmpty(key) ? Loc.Get("UnknownArtist") : key;
}
