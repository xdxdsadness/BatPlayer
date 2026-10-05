using System;
using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Models;

namespace BatPlayer.Helpers;

/// <summary>
/// Построение runtime-треков Track из строк vk_tracks (карточки Home, VK Music, поиск).
/// Такие треки существуют только в памяти: БД tracks не пишется, Id — отрицательные
/// (-1 - index), чтобы гарантированно не пересекаться с локальными long-Id. Чтобы Id
/// не пересекались и с SC-карточками в том же списке, нумерация продолжается после них
/// (startIndex — см. LibraryViewModel.LoadAsync / ApplySearchAsync).
/// Поле Track.ScId при Source="vk" хранит vk_id (универсальный PlatformId — см. Track).
/// </summary>
public static class VkRuntimeTracks
{
    /// <summary>
    /// Карточка VK-трека для списков: FilePath пуст — реальный файл (локальный матч или
    /// mp3 из кэша) резолвится при клике; CoverCachePath — локальная обложка из кэша.
    /// IsFavorite = false: лайков VK в этой модели нет, страница Favorites их не получает.
    /// DateAdded = момент первой синхронизации (VkTracksRepository хранит первый
    /// synced_at): свежесинхронизированный трек встаёт над старыми в общей сортировке.
    /// </summary>
    public static Track BuildRuntimeTrack(VkTrackRow row, int index)
        => BuildRuntimeTrack(row.VkId, row.Title, row.Artist, row.DurationMs, row.ArtworkLocalPath, index,
            TrackTimestamps.ParseUtc(row.SyncedAt));

    /// <summary>Карточка по полям VK-трека (перегрузка для VkCard на странице VK Music).</summary>
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
    /// VK-часть списка библиотеки по фильтру страницы: только «All» (Home). «Recent»
    /// (недавно добавленные), «Played» и «Favorites» — пусто: VK-музыка не имеет дат
    /// добавления/прослушивания в локальной модели и лайков. Порядок rows (порядок
    /// библиотеки VK из al_audio) сохраняется. startIndex — смещение нумерации runtime-Id.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// VK-часть поиска: строки с совпадением title/artist (регистронезависимо) —
    /// runtime-карточки В КОНЦЕ списка (после локальных и SC-совпадений). FilePath пуст:
    /// файл резолвится на клике через AudioService.FilePathResolver. Пустой/пропущенный
    /// запрос — пустой результат (поиск означает непустую строку). Чистая функция —
    /// покрыта юнит-тестами.
    /// </summary>
    public static List<Track> FilterWithVk(int startIndex, IReadOnlyList<VkTrackRow> rows, string? query)
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
