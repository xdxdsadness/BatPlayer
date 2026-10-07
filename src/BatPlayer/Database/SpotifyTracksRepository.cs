using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>Row of the spotify_tracks table (DB schema is fixed, do not change).</summary>
public sealed class SpotifyTrackRow
{
    public string SpotifyId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;
    public string? ArtworkLocalPath { get; set; }
    public bool IsPlayable { get; set; }
    public string AddedAt { get; set; } = string.Empty;
    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>SQLite repository for Spotify Liked Songs (spotify_tracks table).</summary>
public sealed class SpotifyTracksRepository
{
    private readonly SqliteConnection _conn;

    public SpotifyTracksRepository(SqliteConnection conn)
    {
        _conn = conn;
    }

    /// <summary>Batch upsert: artwork_local_path is preserved (local artwork cache).</summary>
    public async Task UpsertBatchAsync(List<SpotifyTrackRow> rows)
    {
        if (rows.Count == 0) return;

        const string sql = "INSERT INTO spotify_tracks (\r\n    spotify_id, title, artist, album, duration_ms,\r\n    artwork_url, artwork_local_path, is_playable, added_at, synced_at\r\n) VALUES (\r\n    @SpotifyId, @Title, @Artist, @Album, @DurationMs,\r\n    @ArtworkUrl, @ArtworkLocalPath, @IsPlayable, @AddedAt, @SyncedAt\r\n)\r\nON CONFLICT(spotify_id) DO UPDATE SET\r\n    title = excluded.title,\r\n    artist = excluded.artist,\r\n    album = excluded.album,\r\n    duration_ms = excluded.duration_ms,\r\n    artwork_url = excluded.artwork_url,\r\n    is_playable = excluded.is_playable,\r\n    added_at = excluded.added_at,\r\n    synced_at = excluded.synced_at";
        await SqlMapper.ExecuteAsync((IDbConnection)_conn, sql, (object)rows);
    }

    public async Task<List<SpotifyTrackRow>> GetAllAsync()
    {
        const string sql = "SELECT * FROM spotify_tracks ORDER BY added_at DESC";
        return SqlMapper.AsList<SpotifyTrackRow>(
            await SqlMapper.QueryAsync<SpotifyTrackRow>((IDbConnection)_conn, sql));
    }

    public async Task SetArtworkLocalPathAsync(string spotifyId, string path)
    {
        const string sql = "UPDATE spotify_tracks SET artwork_local_path = @path WHERE spotify_id = @spotifyId";
        await SqlMapper.ExecuteAsync((IDbConnection)_conn, sql, (object)new { spotifyId, path });
    }

    public async Task DeleteAllAsync()
    {
        await SqlMapper.ExecuteAsync((IDbConnection)_conn, "DELETE FROM spotify_tracks");
    }
}
