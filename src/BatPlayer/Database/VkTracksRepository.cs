using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// One VK track row: metadata only (no audio downloading).
/// vk_id is "{owner_id}_{id}" (a VK track id is unique only together with its owner),
/// stored as TEXT. Temporary mp3 URLs from the al_audio catalog are NOT persisted:
/// they expire quickly and are re-resolved in memory at streaming time
/// (VkService.GetPlayableStreamAsync).
/// </summary>
public sealed class VkTrackRow
{
    public string VkId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Stream URL hash (reload_audio builds the mp3 URL from it).</summary>
    public string UrlHash { get; set; } = string.Empty;

    /// <summary>Cover path in the local cache (artworks_cache/vk_{vk_id}.jpg); null = not downloaded yet.</summary>
    public string? ArtworkLocalPath { get; set; }

    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Repository for the vk_tracks table. Upsert by vk_id, ordered by insertion (rowid):
/// the al_audio catalog order is kept from the first sync; new tracks are appended.
/// </summary>
public sealed class VkTracksRepository
{
    private readonly SqliteConnection _conn;

    public VkTracksRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>
    /// Batch upsert: re-syncing does not duplicate rows. For existing rows synced_at
    /// is KEPT as-is: it means "date the track was added to the library" (unified
    /// newest-first sorting in LibraryViewModel), not the last sync date — otherwise
    /// every re-sync would lift the whole VK catalog above the other sources.
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

    /// <summary>All VK tracks in insertion order (al_audio library order).</summary>
    public async Task<List<VkTrackRow>> GetAllAsync()
    {
        var rows = await _conn.QueryAsync<VkTrackRow>(
            "SELECT * FROM vk_tracks ORDER BY rowid");
        return rows.AsList();
    }

    /// <summary>Track by vk_id, or null — the player resolver needs the row to obtain a stream.</summary>
    public async Task<VkTrackRow?> GetByVkIdAsync(string vkId)
        => await _conn.QueryFirstOrDefaultAsync<VkTrackRow>(
            "SELECT * FROM vk_tracks WHERE vk_id = @vkId", new { vkId });

    public async Task<int> CountAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vk_tracks");

    public async Task ClearAllAsync()
        => await _conn.ExecuteAsync("DELETE FROM vk_tracks");

    /// <summary>
    /// Local artwork path for a track. Separate from UpsertBatch: a metadata re-sync
    /// must not reset already-downloaded paths.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string vkId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE vk_tracks SET artwork_local_path = @path WHERE vk_id = @vkId",
            new { path, vkId });
}
