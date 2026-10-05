using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Сопоставление трека SoundCloud с треком локальной библиотеки.
/// Ключ матча: нормализованный (ArtistHelper.Key) исполнитель + нормализованное название.
/// Нормализация режет различия, которыми реальные имена почти всегда отличаются:
/// регистр, пунктуацию, содержимое скобок («(Official Video)»), суффиксы feat./ft.
/// </summary>
public static partial class MatchHelper
{
    // "(Official Video)", "[HD]", "{...}" — вырезаем вместе с содержимым.
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketRegex();

    // "feat. X", "ft X", "featuring X" — суффикс и всё, что после него.
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$")]
    private static partial Regex FeatRegex();

    /// <summary>Нормализует имя исполнителя: пустые/legacy-значения схлопываются в "" через ArtistHelper.Key.</summary>
    public static string NormalizeArtist(string? artist)
        => Normalize(ArtistHelper.Key(artist));

    /// <summary>
    /// Нормализация строки для матча: lower invariant, вырезание скобок и feat.-хвостов,
    /// всё кроме букв и цифр — в пробел, схлопывание пробелов, trim.
    /// Возвращает "" для пустых строк.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var s = input.ToLowerInvariant();
        s = BracketRegex().Replace(s, " ");
        s = FeatRegex().Replace(s, " ");
        // Не-буквы/цифры (пунктуация, дефисы, «&») → пробел; юникод-буквы (кириллица и т.д.) сохраняются.
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s;
    }

    /// <summary>Ключ матча «исполнитель|название» после нормализации.</summary>
    public static string BuildKey(string? artist, string? title)
        => NormalizeArtist(artist) + "|" + Normalize(title);

    /// <summary>
    /// Ищет локальный трек, соответствующий треку SoundCloud.
    /// 1) точный матч по ключу (артист + название);
    /// 2) fallback: у SC-названия часто вид «Artist - Title» — если после отделения
    ///    префикса совпадает название и исполнитель, берём его.
    /// Возвращает null, если локального матча нет.
    /// </summary>
    public static Track? FindLocalMatch(IEnumerable<Track> localTracks, string? scArtist, string? scTitle)
    {
        var key = BuildKey(scArtist, scTitle);
        Track? byKey = null;
        Track? bySplitTitle = null;

        foreach (var t in localTracks)
        {
            if (byKey == null && BuildKey(t.Artist, t.Title) == key)
                byKey = t;

            if (bySplitTitle == null)
            {
                var (altArtist, altTitle) = SplitEmbeddedTitle(scTitle);
                if (altTitle != null && BuildKey(t.Artist, t.Title) == BuildKey(altArtist, altTitle))
                    bySplitTitle = t;
            }

            if (byKey != null) return byKey;
        }

        return bySplitTitle;
    }

    /// <summary>
    /// Прединдекс локальной библиотеки по ключам матчинга: O(M) на построение,
    /// затем O(1) на каждый лайк/карточку платформы. Заменяет O(N*M) проход
    /// FindLocalMatch при массовых матчах (сотни карточек × сотни-тысячи треков
    /// давали сотни тысяч сравнений строк с нормализацией на каждый вход в бар).
    /// </summary>
    public static MatchIndex BuildIndex(IEnumerable<Track> localTracks)
    {
        var index = new MatchIndex();
        foreach (var t in localTracks)
        {
            var key = BuildKey(t.Artist, t.Title);
            if (key.Length > 0 && !index.ByKey.ContainsKey(key)) index.ByKey[key] = t;

            var (altArtist, altTitle) = SplitEmbeddedTitle(t.Title);
            if (altTitle != null)
            {
                var altKey = BuildKey(altArtist, altTitle);
                if (altKey.Length > 0 && !index.BySplitKey.ContainsKey(altKey)) index.BySplitKey[altKey] = t;
            }
        }
        return index;
    }

    /// <summary>Матч по прединдексу: эквивалентен FindLocalMatch, но O(1).</summary>
    public static Track? FindLocalMatch(MatchIndex index, string? scArtist, string? scTitle)
    {
        var key = BuildKey(scArtist, scTitle);
        if (index.ByKey.TryGetValue(key, out var direct)) return direct;

        var (altArtist, altTitle) = SplitEmbeddedTitle(scTitle);
        if (altTitle != null && index.BySplitKey.TryGetValue(BuildKey(altArtist, altTitle), out var alt))
            return alt;
        return null;
    }

    /// <summary>Индекс матчинга (см. BuildIndex/FindLocalMatch).</summary>
    public sealed class MatchIndex
    {
        public Dictionary<string, Track> ByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Track> BySplitKey { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Разбирает SC-название вида «Artist - Title» на части; null, если шаблона нет.</summary>
    private static (string? Artist, string? Title) SplitEmbeddedTitle(string? scTitle)
    {
        if (string.IsNullOrWhiteSpace(scTitle)) return (null, null);
        var idx = scTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (idx <= 0 || idx + 3 >= scTitle.Length) return (null, null);
        return (scTitle[..idx].Trim(), scTitle[(idx + 3)..].Trim());
    }
}
