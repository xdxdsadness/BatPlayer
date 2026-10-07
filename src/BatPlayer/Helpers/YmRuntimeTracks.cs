using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Builds runtime Track objects from ym_tracks rows (Home, Yandex Music, search cards).
/// Such tracks exist only in memory: the tracks DB is not written, and Ids are negative
/// (-1 - index) so they can never collide with local long Ids. To also avoid collisions
/// with SC/VK cards in the same list, numbering continues after them
/// (startIndex — see LibraryViewModel.LoadAsync / ApplySearchAsync).
/// Track.ScId with Source="yandex" holds ym_id (the universal PlatformId — see Track).
/// </summary>
public static class YmRuntimeTracks
{
    /// <summary>
    /// YM track card for lists: FilePath is empty — the real file (local match or cached
    /// mp3) is resolved on click; CoverCachePath is the local cached cover.
    /// IsFavorite defaults to false: the Favorites page excludes YM unless callers pass true.
    /// IsAvailable = row.Available: tariff-unavailable tracks are flagged to the player.
    /// DateAdded = like time: the unified sorting keeps freshly liked above older ones.
    /// </summary>
    public static Track BuildRuntimeTrack(YmTrackRow row, int index, bool isFavorite = false)
        => BuildRuntimeTrack(row.YmId, row.Title, row.Artist, row.DurationMs,
            row.ArtworkLocalPath, row.Available, index, isFavorite,
            TrackTimestamps.ParseUtc(row.LikedAt));

    /// <summary>Card from YM track fields (overload for YmCard on the Yandex Music page).
    /// isFavorite: like cards (YM page, Favorites) = true so the player heart is lit;
    /// wave recommendation cards = false (they are not liked).</summary>
    public static Track BuildRuntimeTrack(string ymId, string title, string artist, long durationMs,
                                          string? artworkLocalPath, bool available, int index,
                                          bool isFavorite = false, DateTime addedAtUtc = default)
        => new()
        {
            Id = -1 - index,
            FilePath = string.Empty,
            ScId = ymId,
            Title = title,
            Artist = artist,
            DurationTicks = durationMs * TimeSpan.TicksPerMillisecond,
            CoverCachePath = artworkLocalPath,
            IsFavorite = isFavorite,
            IsAvailable = available,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceYandex,
        };

    /// <summary>
    /// YM part of the library list for the page filter: "All" (Home) and "Favorites" —
    /// YM likes are the platform's favorites. "Recent" (newly added) and "Played" are
    /// empty: Yandex Music has no added dates in this model, and "Played" plays come
    /// from play_log (MergeRecentlyPlayed). The rows order (like order) is preserved.
    /// startIndex offsets the runtime-Id numbering.
    /// </summary>
    public static List<Track> BuildYmAppend(string filterMode, IReadOnlyList<YmTrackRow> rows, int startIndex)
    {
        var result = new List<Track>();
        if (!string.Equals(filterMode, "All", StringComparison.Ordinal)
            && !string.Equals(filterMode, "Favorites", StringComparison.Ordinal))
            return result;

        for (int i = 0; i < rows.Count; i++)
            result.Add(BuildRuntimeTrack(rows[i], startIndex + i, isFavorite: true));
        return result;
    }

    /// <summary>
    /// YM part of search: rows matching title/artist (case-insensitive) become runtime
    /// cards at the END of the list (after local, SC and VK matches). FilePath is empty:
    /// resolved on click via AudioService.FilePathResolver. An empty/missing query yields
    /// an empty result (search means a non-empty string).
    /// </summary>
    public static List<Track> FilterWithYm(int startIndex, IReadOnlyList<YmTrackRow> rows, string? query)
        => RuntimeTrackFilter.Filter(startIndex, rows, query,
            r => r.Title, r => r.Artist, (r, i) => BuildRuntimeTrack(r, i));
}
