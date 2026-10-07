using BatPlayer.Localization;
using System.Collections.Generic;
using System.Linq;

namespace BatPlayer.Helpers;

/// <summary>
/// Artist name normalization. Empty and legacy values from old DB rows collapse into
/// a single "" key so all "unknown" artists group together (artists page, profile,
/// name clicks).
/// </summary>
public static partial class ArtistHelper
{
    // Values older app versions wrote to the DB instead of empty tags.
    private static readonly string[] LegacyUnknownArtists =
    {
        "Неизвестный исполнитель",
        "Неизвестный артист",
        "Unknown Artist",
    };

    /// <summary>
    /// Grouping key: "" for unknown artists, otherwise the lowercased name.
    /// Case-insensitivity merges "Kai Angel" and "kai angel" into one artist,
    /// including tracks from different platforms.
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

    /// <summary>Strips the feature part of a name: "X feat. B" → "X", "X (ft. B)" → "X",
    /// "X featuring B" → "X". Canonicalizes the artist family — the tracks
    /// "X feat. B" and "X" belong to the same artist X.</summary>
    public static string StripFeatures(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        var s = FeatParenRegex().Replace(artist, " ");
        return FeatTailRegex().Replace(s, " ").Trim();
    }

    // "(feat. B)", "(with D)", "(ft. B & C)" — full parenthesized feature (incl. SC-style "with").
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\((?:feat\.?|ft\.?|featuring|with)\s+[^)]*\)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FeatParenRegex();

    // "feat. B", "ft B", "featuring B & C" — tail after the feature.
    [System.Text.RegularExpressions.GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FeatTailRegex();

    /// <summary>
    /// Splits an artist string into individual artists: commas and ampersands separate
    /// co-authors ("August, TikoTheCEO" credits a play to BOTH artists), while the
    /// case-insensitive key merges "Kai Angel"/"kai angel" and tracks across platforms.
    /// Returns at most 6 names; duplicates (case-insensitive) are removed.
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

    /// <summary>Display name: an empty key is replaced by the localized string.</summary>
    public static string DisplayName(string key)
        => string.IsNullOrEmpty(key) ? Loc.Get("UnknownArtist") : key;
}
