using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// One SoundCloud like row: metadata only (no audio downloading).
/// sc_id is the string track ID in SoundCloud (numeric in the API, stored as TEXT:
/// a string PK avoids width issues and matches permalink URL formats).
/// </summary>
public sealed class SoundCloudLikeRow
{
    public string ScId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Cover path in the local cache (artworks_cache/{scId}.jpg); null = not downloaded yet.</summary>
    public string? ArtworkLocalPath { get; set; }

    public string PermalinkUrl { get; set; } = string.Empty;
    public bool Streamable { get; set; }
    public string? LikedAt { get; set; }
    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Repository for the soundcloud_likes table. Upsert by sc_id, ordered by liked_at DESC.
/// </summary>
public sealed class SoundCloudLikesRepository
{
    private readonly SqliteConnection _conn;

    public SoundCloudLikesRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>Batch upsert: re-syncing does not duplicate rows.</summary>
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

    /// <summary>All likes ordered by like date (newest first).</summary>
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
    /// Local artwork path for a like. Separate from UpsertBatch: a metadata re-sync
    /// must not reset already-downloaded paths.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string scId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE soundcloud_likes SET artwork_local_path = @path WHERE sc_id = @scId",
            new { path, scId });
}
