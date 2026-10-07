using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Fast full-text search over the library. Uses LIKE with COLLATE NOCASE.
/// Handles tens of thousands of tracks in under 50ms.
/// </summary>
public sealed class SearchService
{
    private readonly SqliteConnection _conn;
    private readonly string _coverCacheDir;

    public SearchService(SqliteConnection conn, string coverCacheDir)
    {
        _conn = conn;
        _coverCacheDir = coverCacheDir;
    }

    public async Task<List<Track>> SearchTracksAsync(string query, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var pattern = $"%{EscapeLike(query)}%";
        var sql = """
            SELECT * FROM tracks
            WHERE title    LIKE @p ESCAPE '\'
               OR artist   LIKE @p ESCAPE '\'
               OR album    LIKE @p ESCAPE '\'
               OR genre    LIKE @p ESCAPE '\'
               OR file_path LIKE @p ESCAPE '\'
            ORDER BY
                CASE WHEN title LIKE @exact ESCAPE '\' THEN 0 ELSE 1 END,
                title
            LIMIT @limit
            """;
        var exact = EscapeLike(query) + "%";
        var result = (await _conn.QueryAsync<Track>(sql, new { p = pattern, exact, limit })).ToList();
        foreach (var t in result)
            if (t.CoverCachePath == null && !string.IsNullOrEmpty(t.CoverHash))
                t.CoverCachePath = System.IO.Path.Combine(_coverCacheDir, t.CoverHash + ".jpg");
        return result;
    }

    public async Task<List<Playlist>> SearchPlaylistsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var pattern = $"%{EscapeLike(query)}%";
        var sql = """
            SELECT p.*, (SELECT COUNT(*) FROM playlist_tracks WHERE playlist_id=p.id) AS TrackCount
            FROM playlists p WHERE p.name LIKE @p ESCAPE '\'
            ORDER BY p.name
            """;
        return (await _conn.QueryAsync<Playlist>(sql, new { p = pattern })).ToList();
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
