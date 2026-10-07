using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Builds runtime Track objects from soundcloud_likes rows (Home, SoundCloud, and
/// artist-profile cards). Such tracks exist only in memory: the tracks DB is not
/// written, and Ids are negative (-1 - index) so they can never collide with local
/// long Ids.
/// </summary>
public static class SoundCloudRuntimeTracks
{
    /// <summary>
    /// Like card for the Home list: FilePath is empty — the real file (local match or
    /// cached mp3) is resolved on click; CoverCachePath is the local cached cover.
    /// DateAdded = like time: LoadAsync sorts the merged list by it, so a freshly
    /// liked SC track lands above older tracks from other sources.
    /// </summary>
    public static Track BuildRuntimeTrack(SoundCloudLikeRow like, string? artworkLocalPath, int index)
        => BuildRuntimeTrack(like.ScId, like.Title, like.Artist, like.DurationMs, artworkLocalPath, index,
            TrackTimestamps.ParseUtc(like.LikedAt));

    /// <summary>
    /// Card from like fields (overload for sources without a SoundCloudLikeRow —
    /// e.g. SoundCloudCard on the SoundCloud page). addedAtUtc is the like date
    /// (unknown for wave/search cards — then "now").
    /// </summary>
    public static Track BuildRuntimeTrack(string scId, string title, string artist, long durationMs,
                                          string? artworkLocalPath, int index,
                                          DateTime addedAtUtc = default)
        => new()
        {
            Id = -1 - index,
            FilePath = string.Empty,
            ScId = scId,
            Title = title,
            Artist = artist,
            DurationTicks = durationMs * TimeSpan.TicksPerMillisecond,
            CoverCachePath = artworkLocalPath,
            IsFavorite = true, // likes
            IsAvailable = true,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceSoundCloud,
        };

    /// <summary>
    /// File-backed variant of the runtime track for the player: FilePath = mp3 from the
    /// disk cache. Id/ScId are preserved so a second click on the card pauses/resumes.
    /// </summary>
    public static Track WithCachedFile(Track runtime, string cachedFilePath)
        => new()
        {
            Id = runtime.Id,
            FilePath = cachedFilePath,
            ScId = runtime.ScId,
            Title = runtime.Title,
            Artist = runtime.Artist,
            DurationTicks = runtime.DurationTicks,
            CoverCachePath = runtime.CoverCachePath,
            IsFavorite = runtime.IsFavorite,
            DateAdded = runtime.DateAdded,
            IsAvailable = true,
            Source = runtime.Source,
        };

    /// <summary>
    /// Whether the track needs path resolution before playback: FilePath is empty and it
    /// is a remote platform track (Source="soundcloud" or "vk") with a known ScId — for
    /// "vk" ScId holds vk_id (see Track.ScId). Local tracks (and platform cards with an
    /// already-resolved file) open as-is.
    /// </summary>
    public static bool NeedsFilePathResolve(Track? track)
        => track != null
           && string.IsNullOrEmpty(track.FilePath)
           && track.IsPlatformTrack
           && !string.IsNullOrEmpty(track.ScId);

    /// <summary>SC likes limit for the "Recently Played" filter — same as the local limit
    /// in LibraryService.GetRecentlyPlayedAsync.</summary>
    public const int RecentlyPlayedScLimit = 50;

    /// <summary>
    /// SC part of the library list for the page filter (Home/Recent/RecentlyPlayed/Favorites).
    /// Likes arrive from the repository already in liked_at DESC order (newest first) —
    /// the order is preserved. "Played" = first 50 likes (the local limit); "Favorites"
    /// and "All" (Home) = all; "Recent" (newly added) = empty: the page is local files only.
    /// </summary>
    public static List<Track> BuildScAppend(string filterMode, IReadOnlyList<SoundCloudLikeRow> likes,
        int startIndex = 0)
    {
        var result = new List<Track>();
        if (string.Equals(filterMode, "Recent", StringComparison.Ordinal)) return result;

        var count = string.Equals(filterMode, "Played", StringComparison.Ordinal)
            ? Math.Min(likes.Count, RecentlyPlayedScLimit)
            : likes.Count;

        for (int i = 0; i < count; i++)
            result.Add(BuildRuntimeTrack(likes[i], likes[i].ArtworkLocalPath, startIndex + i));
        return result;
    }

    /// <summary>
    /// Background prefetch candidate when a track starts: the next queue item
    /// (strictly currentIndex+1) if it is an SC card without a resolved file —
    /// FilePath is empty (the shared FilePathResolver will put the file in the cache,
    /// so the track starts without download lag on switch). Local tracks and SC cards
    /// with a ready file need no prefetch; unavailable ones (IsAvailable=false from a
    /// failed resolve in this session) are not prefetched — auto-advance skips them anyway.
    /// </summary>
    public static Track? GetNextSoundCloudCandidate(IReadOnlyList<Track>? queue, int currentIndex)
    {
        if (queue == null || currentIndex < 0) return null;
        var next = currentIndex + 1;
        if (next >= queue.Count) return null;
        var candidate = queue[next];
        if (!NeedsFilePathResolve(candidate)) return null;
        if (!candidate.IsAvailable) return null;
        return candidate;
    }

    /// <summary>
    /// Library search: local tracks (title/artist/album/genre, case-insensitive) plus
    /// SoundCloud likes matching title/artist — runtime cards at the END of the list
    /// (FilePath empty: resolved on click via AudioService.FilePathResolver).
    /// An empty/missing query returns unfiltered local tracks and no SC cards: an empty
    /// search means the regular page list; likes are added to Home by LoadAsync.
    /// startIndex offsets the runtime-Id numbering (Id = -1 - index) so it collides with
    /// neither local Ids nor previously built SC cards.
    /// </summary>
    public static List<Track> FilterWithSoundCloud(IReadOnlyList<Track> localTracks,
                                                   IReadOnlyList<SoundCloudLikeRow> scLikes,
                                                   string? query, int startIndex)
    {
        var result = new List<Track>();
        if (string.IsNullOrWhiteSpace(query))
        {
            result.AddRange(localTracks);
            return result;
        }

        foreach (var t in localTracks)
        {
            if (t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
             || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
             || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)
             || t.Genre.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(t);
            }
        }

        var scMatches = RuntimeTrackFilter.Filter(startIndex, scLikes, query,
            r => r.Title, r => r.Artist, (r, i) => BuildRuntimeTrack(r, r.ArtworkLocalPath, i));
        result.AddRange(scMatches);

        return result;
    }
}
