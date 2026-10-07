using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Builds runtime Track objects from vk_tracks rows (Home, VK Music, search cards).
/// Such tracks exist only in memory: the tracks DB is not written, and Ids are negative
/// (-1 - index) so they can never collide with local long Ids. To also avoid collisions
/// with SC cards in the same list, numbering continues after them
/// (startIndex — see LibraryViewModel.LoadAsync / ApplySearchAsync).
/// Track.ScId with Source="vk" holds vk_id (the universal PlatformId — see Track).
/// </summary>
public static class VkRuntimeTracks
{
    /// <summary>
    /// VK track card for lists: FilePath is empty — the real file (local match or cached
    /// mp3) is resolved on click; CoverCachePath is the local cached cover.
    /// IsFavorite = false: VK has no likes in this model, the Favorites page excludes them.
    /// DateAdded = first sync moment (VkTracksRepository keeps the first synced_at), so a
    /// freshly synced track lands above older ones in the unified sorting.
    /// </summary>
    public static Track BuildRuntimeTrack(VkTrackRow row, int index)
        => BuildRuntimeTrack(row.VkId, row.Title, row.Artist, row.DurationMs, row.ArtworkLocalPath, index,
            TrackTimestamps.ParseUtc(row.SyncedAt));

    /// <summary>Card from VK track fields (overload for VkCard on the VK Music page).</summary>
    public static Track BuildRuntimeTrack(string vkId, string title, string artist, long durationMs,
                                          string? artworkLocalPath, int index,
                                          DateTime addedAtUtc = default)
        => new()
        {
            Id = -1 - index,
            FilePath = string.Empty,
            ScId = vkId,
            Title = title,
            Artist = artist,
            DurationTicks = durationMs * TimeSpan.TicksPerMillisecond,
            CoverCachePath = artworkLocalPath,
            IsFavorite = false,
            IsAvailable = true,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceVk,
        };

    /// <summary>
    /// VK part of the library list for the page filter: "All" (Home) only. "Recent"
    /// (newly added), "Played" and "Favorites" are empty: VK music has no added/played
    /// dates or likes in the local model. The rows order (al_audio library order) is
    /// preserved. startIndex offsets the runtime-Id numbering.
    /// </summary>
    public static List<Track> BuildVkAppend(string filterMode, IReadOnlyList<VkTrackRow> rows, int startIndex)
    {
        var result = new List<Track>();
        if (!string.Equals(filterMode, "All", StringComparison.Ordinal)) return result;

        for (int i = 0; i < rows.Count; i++)
            result.Add(BuildRuntimeTrack(rows[i], startIndex + i));
        return result;
    }

    /// <summary>
    /// VK part of search: rows matching title/artist (case-insensitive) become runtime
    /// cards at the END of the list (after local and SC matches). FilePath is empty:
    /// resolved on click via AudioService.FilePathResolver. An empty/missing query yields
    /// an empty result (search means a non-empty string).
    /// </summary>
    public static List<Track> FilterWithVk(int startIndex, IReadOnlyList<VkTrackRow> rows, string? query)
        => RuntimeTrackFilter.Filter(startIndex, rows, query,
            r => r.Title, r => r.Artist, BuildRuntimeTrack);
}
