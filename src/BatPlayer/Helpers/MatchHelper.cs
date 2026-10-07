using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Matches a SoundCloud track against a local library track.
/// Match key: normalized (ArtistHelper.Key) artist + normalized title.
/// Normalization removes the differences real names almost always have:
/// case, punctuation, bracketed content ("(Official Video)"), feat./ft. suffixes.
/// </summary>
public static partial class MatchHelper
{
    // "(Official Video)", "[HD]", "{...}" — removed together with contents.
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketRegex();

    // "feat. X", "ft X", "featuring X" — suffix and everything after it.
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$")]
    private static partial Regex FeatRegex();

    /// <summary>Normalizes an artist name: empty/legacy values collapse to "" via ArtistHelper.Key.</summary>
    public static string NormalizeArtist(string? artist)
        => Normalize(ArtistHelper.Key(artist));

    /// <summary>
    /// Normalizes a string for matching: lower invariant, brackets and feat. tails removed,
    /// everything except letters and digits becomes a space, spaces collapsed, trimmed.
    /// Returns "" for empty input.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var s = input.ToLowerInvariant();
        s = BracketRegex().Replace(s, " ");
        s = FeatRegex().Replace(s, " ");
        // Non-alphanumerics (punctuation, hyphens, "&") → space; unicode letters (incl. Cyrillic) are kept.
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s;
    }

    /// <summary>Match key "artist|title" after normalization.</summary>
    public static string BuildKey(string? artist, string? title)
        => NormalizeArtist(artist) + "|" + Normalize(title);

    /// <summary>
    /// Finds a local track matching a SoundCloud track.
    /// 1) exact match by key (artist + title);
    /// 2) fallback: an SC title is often "Artist - Title" — if title and artist match
    ///    after splitting off the prefix, that track is used.
    /// Returns null when there is no local match.
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
    /// Pre-indexes the local library by match keys: O(M) to build, then O(1) per like or
    /// platform card. Replaces the O(N*M) FindLocalMatch pass for bulk matching
    /// (hundreds of cards x hundreds/thousands of tracks used to cost hundreds of
    /// thousands of normalized string comparisons on every search-bar input).
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

    /// <summary>Match via the pre-index: equivalent to FindLocalMatch but O(1).</summary>
    public static Track? FindLocalMatch(MatchIndex index, string? scArtist, string? scTitle)
    {
        var key = BuildKey(scArtist, scTitle);
        if (index.ByKey.TryGetValue(key, out var direct)) return direct;

        var (altArtist, altTitle) = SplitEmbeddedTitle(scTitle);
        if (altTitle != null && index.BySplitKey.TryGetValue(BuildKey(altArtist, altTitle), out var alt))
            return alt;
        return null;
    }

    /// <summary>Match index (see BuildIndex/FindLocalMatch).</summary>
    public sealed class MatchIndex
    {
        public Dictionary<string, Track> ByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Track> BySplitKey { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Splits an SC title of the form "Artist - Title"; null if the pattern is absent.</summary>
    private static (string? Artist, string? Title) SplitEmbeddedTitle(string? scTitle)
    {
        if (string.IsNullOrWhiteSpace(scTitle)) return (null, null);
        var idx = scTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (idx <= 0 || idx + 3 >= scTitle.Length) return (null, null);
        return (scTitle[..idx].Trim(), scTitle[(idx + 3)..].Trim());
    }
}
