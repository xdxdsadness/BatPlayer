using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Построение runtime-треков Track из строк ym_tracks (карточки Home, Яндекс Музыка, поиск).
/// Такие треки существуют только в памяти: БД tracks не пишется, Id — отрицательные
/// (-1 - index), чтобы гарантированно не пересекаться с локальными long-Id. Чтобы Id
/// не пересекались и с SC/VK-карточками в том же списке, нумерация продолжается после них
/// (startIndex — см. LibraryViewModel.LoadAsync / ApplySearchAsync).
/// Поле Track.ScId при Source="yandex" хранит ym_id (универсальный PlatformId — см. Track).
/// </summary>
public static class YmRuntimeTracks
{
    /// <summary>
    /// Карточка YM-трека для списков: FilePath пуст — реальный файл (локальный матч или
    /// mp3 из кэша) резолвится при клике; CoverCachePath — локальная обложка из кэша.
    /// IsFavorite = false: лайков YM в этой модели нет, страница Favorites их не получает.
    /// IsAvailable = row.Available: тарифно-недоступные треки помечаются плееру.
    /// DateAdded = время лайка: общая сортировка держит свежелайкнутые над старыми.
    /// </summary>
    public static Track BuildRuntimeTrack(YmTrackRow row, int index, bool isFavorite = false)
        => BuildRuntimeTrack(row.YmId, row.Title, row.Artist, row.DurationMs,
            row.ArtworkLocalPath, row.Available, index, isFavorite,
            TrackTimestamps.ParseUtc(row.LikedAt));

    /// <summary>Карточка по полям YM-трека (перегрузка для YmCard на странице Яндекс Музыки).
    /// isFavorite: карточки-ЛАЙКИ (страница ЯМ, «Фавориты») — true — сердечко в плеере
    /// горит; карточки-рекомендации волны — false (они не лайкнуты).</summary>
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
    /// YM-часть списка библиотеки по фильтру страницы: «All» (Home) и «Favorites» —
    /// лайки ЯМ и есть фавориты платформы. «Recent» (недавно добавленные) и «Played»
    /// — пусто: у Яндекс Музыки в этой модели нет дат добавления, а прослушки на
    /// «Played» приходят из play_log (MergeRecentlyPlayed). Порядок rows (порядок
    /// лайков) сохраняется. startIndex — смещение нумерации runtime-Id.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// YM-часть поиска: строки с совпадением title/artist (регистронезависимо) —
    /// runtime-карточки В КОНЦЕ списка (после локальных, SC- и VK-совпадений). FilePath пуст:
    /// файл резолвится на клике через AudioService.FilePathResolver. Пустой/пропущенный
    /// запрос — пустой результат (поиск означает непустую строку). Чистая функция —
    /// покрыта юнит-тестами.
    /// </summary>
    public static List<Track> FilterWithYm(int startIndex, IReadOnlyList<YmTrackRow> rows, string? query)
    {
        var result = new List<Track>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var index = startIndex;
        foreach (var row in rows)
        {
            if (row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
             || row.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(BuildRuntimeTrack(row, index));
                index++;
            }
        }
        return result;
    }
}
