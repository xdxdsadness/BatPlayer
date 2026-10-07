using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// One Yandex Music track row: metadata only (no audio downloading).
/// ym_id is the numeric track id in the Yandex Music API, stored as TEXT. Temporary
/// mp3 URLs from download-info are NOT persisted: they expire quickly and are
/// re-resolved in memory at streaming time (YmService.GetStreamUrlAsync). Available is
/// the tariff-availability flag (API field): unavailable tracks stay visible but dimmed.
/// </summary>
public sealed class YmTrackRow
{
    public string YmId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Cover path in the local cache (artworks_cache/ym_{ym_id}.jpg); null = not downloaded yet.</summary>
    public string? ArtworkLocalPath { get; set; }

    public bool Available { get; set; } = true;

    /// <summary>Like time from the API (ISO 8601): the catalog is sorted by it — newest
    /// likes first. null for rows synced before the column existed.</summary>
    public string? LikedAt { get; set; }

    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Repository for the ym_tracks table. Upsert by ym_id, ordered by like time (newest
/// first, as in Yandex Music); rows without a like time (old syncs) go last.
/// </summary>
public sealed class YmTracksRepository
{
    private readonly SqliteConnection _conn;

    public YmTracksRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>Batch upsert: re-syncing does not duplicate rows (artwork_local_path is
    /// preserved on re-sync; liked_at is updated with the like time from the API).</summary>
    public async Task UpsertBatchAsync(IEnumerable<YmTrackRow> rows)
    {
        var sql = """
            INSERT INTO ym_tracks(ym_id, title, artist, duration_ms, artwork_url, available, liked_at, synced_at)
            VALUES(@YmId, @Title, @Artist, @DurationMs, @ArtworkUrl, @Available, @LikedAt, @SyncedAt)
            ON CONFLICT(ym_id) DO UPDATE SET
                title       = @Title,
                artist      = @Artist,
                duration_ms = @DurationMs,
                artwork_url = @ArtworkUrl,
                available   = @Available,
                liked_at    = COALESCE(NULLIF(@LikedAt, ''), ym_tracks.liked_at),
                synced_at   = @SyncedAt
            """;
        await _conn.ExecuteAsync(sql, rows);
    }

    /// <summary>All Yandex Music tracks by like time — newest first, as in the app.
    /// Rows without liked_at (synced before the column existed) go last.</summary>
    public async Task<List<YmTrackRow>> GetAllAsync()
    {
        var rows = await _conn.QueryAsync<YmTrackRow>(
            "SELECT * FROM ym_tracks ORDER BY liked_at DESC, rowid ASC");
        return rows.AsList();
    }

    public async Task<int> CountAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ym_tracks");

    /// <summary>How many rows still lack liked_at: after migration v7 the like time only
    /// appears via sync — the page uses this count to run a catch-up sync once.</summary>
    public async Task<int> CountWithoutLikedAtAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ym_tracks WHERE liked_at IS NULL");

    public async Task ClearAllAsync()
        => await _conn.ExecuteAsync("DELETE FROM ym_tracks");

    /// <summary>
    /// Delete tracks no longer among the current likes: unliked entries disappear on
    /// sync instead of accumulating forever. Returns the number deleted.
    /// </summary>
    public async Task<int> DeleteNotInAsync(IEnumerable<string> keepYmIds)
    {
        var ids = keepYmIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (ids.Count == 0) return 0;

        await _conn.ExecuteAsync("CREATE TEMP TABLE IF NOT EXISTS sync_keep(ym_id TEXT PRIMARY KEY)");
        await _conn.ExecuteAsync("DELETE FROM sync_keep");
        await _conn.ExecuteAsync("INSERT INTO sync_keep(ym_id) VALUES(@id)",
            ids.Select(id => new { id }));
        var deleted = await _conn.ExecuteAsync(
            "DELETE FROM ym_tracks WHERE ym_id NOT IN (SELECT ym_id FROM sync_keep)");
        await _conn.ExecuteAsync("DROP TABLE sync_keep");
        return deleted;
    }

    /// <summary>
    /// Local artwork path for a track. Separate from UpsertBatch: a metadata re-sync
    /// must not reset already-downloaded paths.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string ymId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE ym_tracks SET artwork_local_path = @path WHERE ym_id = @ymId",
            new { path, ymId });
}
