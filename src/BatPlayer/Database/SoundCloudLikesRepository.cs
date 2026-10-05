using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// Одна запись лайка SoundCloud: только метаданные (скачивание аудио не предусмотрено).
/// sc_id — строковый ID трека в SoundCloud (в API он числовой, но храним как TEXT:
/// строковый PK не зависит от разрядности и совпадает с formatом permalink-ссылок).
/// </summary>
public sealed class SoundCloudLikeRow
{
    public string ScId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Путь обложки в локальном кэше (artworks_cache/{scId}.jpg); null — ещё не скачана.</summary>
    public string? ArtworkLocalPath { get; set; }

    public string PermalinkUrl { get; set; } = string.Empty;
    public bool Streamable { get; set; }
    public string? LikedAt { get; set; }
    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Репозиторий таблицы soundcloud_likes. Upsert по sc_id, выдача в порядке liked_at DESC.
/// </summary>
public sealed class SoundCloudLikesRepository
{
    private readonly SqliteConnection _conn;

    public SoundCloudLikesRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>Пакетный upsert: повторная синхронизация не дублирует записи.</summary>
    public async Task UpsertBatchAsync(IEnumerable<SoundCloudLikeRow> rows)
    {
        var sql = """
            INSERT INTO soundcloud_likes(sc_id, title, artist, duration_ms, artwork_url,
                                         permalink_url, streamable, liked_at, synced_at)
            VALUES(@ScId, @Title, @Artist, @DurationMs, @ArtworkUrl,
                   @PermalinkUrl, @Streamable, @LikedAt, @SyncedAt)
            ON CONFLICT(sc_id) DO UPDATE SET
                title         = @Title,
                artist        = @Artist,
                duration_ms   = @DurationMs,
                artwork_url   = @ArtworkUrl,
                permalink_url = @PermalinkUrl,
                streamable    = @Streamable,
                liked_at      = @LikedAt,
                synced_at     = @SyncedAt
            """;
        await _conn.ExecuteAsync(sql, rows);
    }

    /// <summary>Все лайки в порядке даты лайка (новые сверху).</summary>
    public async Task<List<SoundCloudLikeRow>> GetAllAsync()
    {
        var rows = await _conn.QueryAsync<SoundCloudLikeRow>(
            "SELECT * FROM soundcloud_likes ORDER BY liked_at DESC");
        return rows.AsList();
    }

    public async Task<int> CountAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM soundcloud_likes");

    public async Task ClearAllAsync()
        => await _conn.ExecuteAsync("DELETE FROM soundcloud_likes");

    /// <summary>
    /// Путь локальной обложки для лайка. Отдельно от UpsertBatch: пере-синк метаданных
    /// не должен сбрасывать уже скачанные пути.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string scId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE soundcloud_likes SET artwork_local_path = @path WHERE sc_id = @scId",
            new { path, scId });
}
