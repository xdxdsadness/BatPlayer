using System;
using System.Collections.Generic;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Shared tail of the platform search filters: rows matching title/artist
/// (case-insensitive) become runtime cards appended after local matches.
/// An empty/missing query yields an empty result.
/// </summary>
public static class RuntimeTrackFilter
{
    public static List<Track> Filter<T>(
        int startIndex, IReadOnlyList<T> rows, string? query,
        Func<T, string> title, Func<T, string> artist, Func<T, int, Track> build)
    {
        var result = new List<Track>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var index = startIndex;
        foreach (var row in rows)
        {
            if (title(row).Contains(query, StringComparison.OrdinalIgnoreCase)
             || artist(row).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(build(row, index));
                index++;
            }
        }
        return result;
    }
}
