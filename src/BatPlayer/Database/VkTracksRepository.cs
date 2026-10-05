using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// Одна запись трека VK: только метаданные (скачивание аудио не предусмотрено).
/// vk_id — "{owner_id}_{id}" (VK-идентификатор трека уникален только в паре с владельцем),
/// храним TEXT. Временные mp3-ссылки из каталога al_audio в БД НЕ пишутся: они живут недолго и
/// разрешаются заново в памяти при стриминге (VkService.GetPlayableStreamAsync).
/// </summary>
public sealed class VkTrackRow
{
    public string VkId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Хеш ссылки на поток (reload_audio строит mp3-ссылку из него).</summary>
    public string UrlHash { get; set; } = string.Empty;

    /// <summary>Путь обложки в локальном кэше (artworks_cache/vk_{vk_id}.jpg); null — ещё не скачана.</summary>
    public string? ArtworkLocalPath { get; set; }

    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Репозиторий таблицы vk_tracks. Upsert по vk_id, выдача в порядке добавления (rowid):
/// порядок каталога al_audio сохраняется при первом синке, новые треки дописываются в конец.
/// </summary>
public sealed class VkTracksRepository
{
    private readonly SqliteConnection _conn;

    public VkTracksRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>
    /// Пакетный upsert: повторная синхронизация не дублирует записи. У уже существующей
    /// строки synced_at СОХРАНЯЕТСЯ прежний: это «дата добавления трека в библиотеку»
    /// (единая сортировка «новые сверху» в LibraryViewModel), а не дата последнего синка —
    /// иначе каждый пересинк поднимал бы весь каталог VK над остальными источниками.
    /// </summary>
    public async Task UpsertBatchAsync(IEnumerable<VkTrackRow> rows)
    {
        var sql = """
            INSERT INTO vk_tracks(vk_id, title, artist, duration_ms, artwork_url, url_hash, synced_at)
            VALUES(@VkId, @Title, @Artist, @DurationMs, @ArtworkUrl, @UrlHash, @SyncedAt)
            ON CONFLICT(vk_id) DO UPDATE SET
                title       = @Title,
                artist      = @Artist,
                duration_ms = @DurationMs,
                artwork_url = @ArtworkUrl,
                url_hash    = @UrlHash,
                synced_at   = vk_tracks.synced_at
            """;
        await _conn.ExecuteAsync(sql, rows);
    }

    /// <summary>Все треки VK в порядке добавления (порядок библиотеки из al_audio).</summary>
    public async Task<List<VkTrackRow>> GetAllAsync()
    {
        var rows = await _conn.QueryAsync<VkTrackRow>(
            "SELECT * FROM vk_tracks ORDER BY rowid");
        return rows.AsList();
    }

    /// <summary>Трек по vk_id или null — резолверу плеера нужна строка для получения стрима.</summary>
    public async Task<VkTrackRow?> GetByVkIdAsync(string vkId)
        => await _conn.QueryFirstOrDefaultAsync<VkTrackRow>(
            "SELECT * FROM vk_tracks WHERE vk_id = @vkId", new { vkId });

    public async Task<int> CountAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vk_tracks");

    public async Task ClearAllAsync()
        => await _conn.ExecuteAsync("DELETE FROM vk_tracks");

    /// <summary>
    /// Путь локальной обложки для трека. Отдельно от UpsertBatch: пере-синк метаданных
    /// не должен сбрасывать уже скачанные пути.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string vkId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE vk_tracks SET artwork_local_path = @path WHERE vk_id = @vkId",
            new { path, vkId });
}
