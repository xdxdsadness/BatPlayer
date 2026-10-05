using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Сервис плейлистов с поддержкой импорта/экспорта M3U/M3U8.
/// </summary>
public sealed class PlaylistService
{
    private readonly SqliteConnection _conn;

    public PlaylistService(SqliteConnection conn) => _conn = conn;

    public async Task<long> CreatePlaylistAsync(string name)
    {
        var now = DateTime.UtcNow.ToString("o");
        var id = await _conn.ExecuteScalarAsync<long>(
            "INSERT INTO playlists(name, created_at, updated_at) VALUES(@n,@c,@u); SELECT last_insert_rowid();",
            new { n = name, c = now, u = now });
        return id;
    }

    public async Task ExportM3UAsync(long playlistId, string outputPath)
    {
        var tracks = await GetPlaylistTracksWithPathsAsync(playlistId);
        await using var w = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);
        await w.WriteLineAsync("#EXTM3U");
        foreach (var t in tracks)
        {
            await w.WriteLineAsync($"#EXTINF:{(int)t.duration.TotalSeconds},{t.artist} - {t.title}");
            await w.WriteLineAsync(t.file_path);
        }
    }

    public async Task<long> ImportM3UAsync(string m3uPath, string playlistName)
    {
        var pid = await CreatePlaylistAsync(playlistName);
        var lines = await File.ReadAllLinesAsync(m3uPath);
        int pos = 0;
        string? pendingTitle = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                pendingTitle = comma >= 0 ? line[(comma + 1)..] : null;
                continue;
            }
            if (line.StartsWith("#")) continue;

            var path = line;
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(m3uPath)!, path));

            if (!File.Exists(path)) continue;

            var trackId = await _conn.ExecuteScalarAsync<long?>(
                "SELECT id FROM tracks WHERE file_path=@p", new { p = path });
            if (trackId == null) continue;

            await _conn.ExecuteAsync(
                "INSERT INTO playlist_tracks(playlist_id, track_id, position) VALUES(@p,@t,@pos)",
                new { p = pid, t = trackId, pos = pos++ });
        }
        return pid;
    }

    private async Task<List<(long id, string title, string artist, string file_path, TimeSpan duration)>> GetPlaylistTracksWithPathsAsync(long pid)
    {
        var rows = await _conn.QueryAsync<(long id, string title, string artist, string file_path, long duration_ticks)>(
            @"SELECT t.id, t.title, t.artist, t.file_path, t.duration_ticks
              FROM tracks t JOIN playlist_tracks pt ON pt.track_id = t.id
              WHERE pt.playlist_id = @p ORDER BY pt.position", new { p = pid });
        return rows.Select(r => (r.id, r.title, r.artist, r.file_path, TimeSpan.FromTicks(r.duration_ticks))).ToList();
    }
}
