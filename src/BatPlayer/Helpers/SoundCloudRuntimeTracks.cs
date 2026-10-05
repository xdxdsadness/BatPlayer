using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Построение runtime-треков Track из строк soundcloud_likes (карточки Home, SoundCloud,
/// «Загрузок» и профиля артиста). Такие треки существуют только в памяти: БД tracks не
/// пишется, Id — отрицательные (-1 - index), чтобы гарантированно не пересекаться
/// с локальными long-Id.
/// </summary>
public static class SoundCloudRuntimeTracks
{
    /// <summary>
    /// Карточка лайка для списка Home: FilePath пуст — реальный файл (локальный матч или
    /// mp3 из кэша) резолвится при клике; CoverCachePath — локальная обложка из кэша.
    /// DateAdded = время лайка: LoadAsync сортирует объединённый список по ней —
    /// свежелайкнутый SC-трек встаёт над старыми треками других источников.
    /// </summary>
    public static Track BuildRuntimeTrack(SoundCloudLikeRow like, string? artworkLocalPath, int index)
        => BuildRuntimeTrack(like.ScId, like.Title, like.Artist, like.DurationMs, artworkLocalPath, index,
            TrackTimestamps.ParseUtc(like.LikedAt));

    /// <summary>
    /// Карточка по полям лайка (перегрузка для источников без SoundCloudLikeRow —
    /// например SoundCloudCard на странице SoundCloud). addedAtUtc — дата лайка
    /// (неизвестна у карточек волны/поиска — тогда «сейчас»).
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
            IsFavorite = true, // лайки
            IsAvailable = true,
            DateAdded = addedAtUtc == default ? DateTime.UtcNow : addedAtUtc,
            Source = Track.SourceSoundCloud,
        };

    /// <summary>
    /// Файловый вариант runtime-трека для плеера: FilePath = mp3 из дискового кэша.
    /// Id/ScId сохраняются, чтобы повторный клик по карточке давал паузу/возобновление.
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
    /// Нужен ли треку резолв пути перед воспроизведением: FilePath пуст и это трек
    /// удалённой платформы (Source="soundcloud" или "vk") с известным ScId — при "vk"
    /// ScId хранит vk_id (см. Track.ScId). Чистая функция — покрыта тестами; локальные
    /// треки (и платформенные карточки с уже резолвнутым файлом) открываются как есть.
    /// </summary>
    public static bool NeedsFilePathResolve(Track? track)
        => track != null
           && string.IsNullOrEmpty(track.FilePath)
           && track.IsPlatformTrack
           && !string.IsNullOrEmpty(track.ScId);

    /// <summary>Лимит SC-лайков для фильтра «Недавно игравшиеся» — как локальный лимит
    /// LibraryService.GetRecentlyPlayedAsync.</summary>
    public const int RecentlyPlayedScLimit = 50;

    /// <summary>
    /// SC-часть списка библиотеки по фильтру страницы (Home/Recent/RecentlyPlayed/Favorites).
    /// Лайки приходят из репозитория уже в порядке liked_at DESC (новые сверху) — порядок
    /// сохраняется. Чистая функция — покрыта юнит-тестами:
    /// «Played» — первые 50 лайков (как локальный лимит); «Favorites» и «All» (Home) — все;
    /// «Recent» (недавно добавленные) — пусто: страница только про локальные файлы.
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
    /// Кандидат фоновой предзагрузки (префетч) при старте трека: следующий элемент
    /// очереди (строго currentIndex+1), если это ещё не резолвнутая SC-карточка —
    /// FilePath пуст (файл доберёт общий FilePathResolver в кэш, к моменту
    /// переключения трек стартует без лага скачивания). Локальные треки и SC-карточки
    /// с готовым файлом предзагрузки не требуют; помеченные недоступными
    /// (IsAvailable=false от неудачного резолва в этой сессии) не префетчатся:
    /// авто-переход их всё равно скипает. Чистая функция — покрыта юнит-тестами.
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
    /// Поиск по библиотеке: локальные треки (title/artist/album/genre, регистронезависимо)
    /// плюс совпавшие по title/artist лайки SoundCloud — runtime-карточки в КОНЦЕ списка
    /// (FilePath пуст: файл резолвится на клике через AudioService.FilePathResolver).
    /// Пустой/пропущенный запрос — локальные без фильтра и без SC-карточек: пустой поиск
    /// означает обычный список страницы, а лайки на Home добавляет LoadAsync.
    /// startIndex — смещение нумерации runtime-Id (Id = -1 - index): не пересекается
    /// ни с локальными Id, ни с SC-карточками, построенными ранее.
    /// Чистая функция — покрыта юнит-тестами.
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

        var scIndex = startIndex;
        foreach (var like in scLikes)
        {
            if (like.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
             || like.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(BuildRuntimeTrack(like, like.ArtworkLocalPath, scIndex));
                scIndex++;
            }
        }

        return result;
    }
}
