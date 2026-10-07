using System;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Builds runtime tracks for the Spotify Saved Tracks page.
/// FilePath is empty — resolved on every queue advance via AudioService.FilePathResolver
/// (local match or failure: the Spotify API does not allow streaming).
/// </summary>
public static class SpotifyRuntimeTracks
{
    /// <param name="addedAtUtc">Saved Tracks added date (added_at from the DB): participates
    /// in the unified newest-first sorting (passed by the caller, which knows it).</param>
    public static Track BuildRuntimeTrack(string spotifyId, string title, string artist,
                                         long durationMs, string? artworkLocalPath, int queueIndex,
                                         DateTime addedAtUtc = default)
    {
        return new Track
        {
            Id = queueIndex,
            Title = title,
            Artist = artist,
            Album = string.Empty,
            DurationTicks = durationMs * TimeSpan.TicksPerMillisecond,
            FilePath = string.Empty,      // resolved at playback
            CoverCachePath = artworkLocalPath,
            ScId = spotifyId,             // universal platform ID
            IsFavorite = true,            // all Saved Tracks are likes
            IsAvailable = true,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceSpotify,
        };
    }
}
