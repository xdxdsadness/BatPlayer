using System;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Построение runtime-треков для страницы Spotify Saved Tracks.
/// FilePath пуст — резолвится на каждом переходе по очереди через AudioService.FilePathResolver
/// (локальный матч или ошибка: Spotify API не позволяет стриминг).
/// </summary>
public static class SpotifyRuntimeTracks
{
    /// <param name="addedAtUtc">Дата добавления в Saved Tracks (added_at из БД): участвует
    /// в общей сортировке «новые сверху» (передаётся вызывающим кодом, где она известна).</param>
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
            FilePath = string.Empty,      // резолвится при воспроизведении
            CoverCachePath = artworkLocalPath,
            ScId = spotifyId,             // универсальный ID платформы
            IsFavorite = true,            // все Saved Tracks — лайки
            IsAvailable = true,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceSpotify,
        };
    }
}
