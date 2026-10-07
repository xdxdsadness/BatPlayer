using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Helpers;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>Aggregated statistics snapshot for a period.</summary>
public sealed class StatsResult
{
    /// <summary>How many DIFFERENT tracks were listened to.</summary>
    public int UniqueTracks { get; init; }

    /// <summary>Total number of listens (including repeats) for the period.</summary>
    public int Plays { get; init; }

    /// <summary>Total time of listened music, hours.</summary>
    public double Hours { get; init; }

    /// <summary>Top-5 artists by play count.</summary>
    public List<(string Artist, int Plays)> TopArtists { get; init; } = new();

    /// <summary>Top-10 tracks by play count.</summary>
    public List<(string Title, string Artist, int Plays)> TopTracks { get; init; } = new();

    /// <summary>Track most often put on repeat (null — nothing to show).</summary>
    public (string Title, string Artist, int Plays)? MostReplayed { get; init; }
}

/// <summary>Statistics period.</summary>
public enum StatsPeriod
{
    Day,
    Week,
    Month,
    Year,
    All
}

public sealed class HistoryService
{
    private readonly SqliteConnection _conn;

    public HistoryService(SqliteConnection conn) => _conn = conn;

    /// <summary>A new play was logged: an open "Recently played" page re-reads the
    /// list (new track on top, the 50th drops off) without revisiting.</summary>
    public event Action? PlayLogged;

    public async Task RecordPlayAsync(long trackId)
    {
        var now = DateTime.UtcNow.ToString("o");
        var sql = """
            INSERT INTO history(track_id, played_at, play_count, last_position_ticks)
            VALUES(@id, @now, 1, 0)
            ON CONFLICT(track_id) DO UPDATE SET
                played_at=@now,
                play_count=play_count+1
            """;
        await _conn.ExecuteAsync(sql, new { id = trackId, now });

        await _conn.ExecuteAsync(
            "UPDATE tracks SET last_played=@now, play_count=play_count+1 WHERE id=@id",
            new { now, id = trackId });
    }

    /// <summary>
    /// Records a listen: for library tracks the aggregates (history/tracks) are
    /// updated; for ALL tracks a row is written to play_log with a SNAPSHOT of the
    /// track (+platform_id and artwork_path: listens outside the catalog — "My wave"
    /// recommendations not present among likes — are restored on "Recently played"
    /// from the snapshot). Platform runtime cards (SoundCloud/VK/Yandex Music) have
    /// negative Ids and are absent from the tracks table — the history insert with
    /// FK used to fail (SQLite Error 19) and interrupt logging, so aggregates are
    /// skipped for them.
    /// </summary>
    public async Task RecordPlayAsync(Track track)
    {
        if (track.Id > 0)
            await RecordPlayAsync(track.Id);

        var now = DateTime.UtcNow.ToString("o");
        await _conn.ExecuteAsync("""
            INSERT INTO play_log(track_title, track_artist, duration_ms, source, platform_id, artwork_path, played_at)
            VALUES(@title, @artist, @durationMs, @source, @platformId, @artworkPath, @now)
            """,
            new
            {
                title = track.Title ?? string.Empty,
                artist = track.Artist ?? string.Empty,
                durationMs = Math.Max(0, track.DurationTicks / TimeSpan.TicksPerMillisecond),
                source = track.Source ?? Track.SourceLocal,
                platformId = track.ScId ?? string.Empty,
                artworkPath = track.CoverCachePath,
                now
            });

        PlayLogged?.Invoke();
    }

    public async Task UpdatePlayStateAsync(long trackId, TimeSpan position, bool isPlaying)
    {
        var pos = position.Ticks;
        await _conn.ExecuteAsync(
            "UPDATE history SET last_position_ticks=@pos WHERE track_id=@id",
            new { pos, id = trackId });

        await _conn.ExecuteAsync(
            "UPDATE tracks SET last_position_ticks=@pos WHERE id=@id",
            new { pos, id = trackId });
    }

    // ============================ Statistics ============================

    private static string SinceUtcIso(StatsPeriod period) => period switch
    {
        StatsPeriod.Day => DateTime.UtcNow.AddDays(-1).ToString("o"),
        StatsPeriod.Week => DateTime.UtcNow.AddDays(-7).ToString("o"),
        StatsPeriod.Month => DateTime.UtcNow.AddDays(-30).ToString("o"),
        StatsPeriod.Year => DateTime.UtcNow.AddDays(-365).ToString("o"),
        _ => "0001-01-01T00:00:00.0000000"
    };

    /// <summary>
    /// Full statistics snapshot for a period. Aggregation in C# (not SQL) so that
    /// featured artists can be split via ArtistHelper.Split: a string like
    /// "August, TikoTheCEO" counts as a play for BOTH artists, and the
    /// case-insensitive key merges "Kai Angel"/"kai angel" across tracks from
    /// different platforms.
    /// All work runs on a background thread via a SEPARATE read-only connection:
    /// Microsoft.Data.Sqlite async methods are actually synchronous, and the query
    /// + play_log aggregation on the shared UI connection froze the UI during the
    /// recompute (including blocking player position ticks). The separate
    /// connection also avoids contention with listen writes on the shared one.
    /// </summary>
    public async Task<StatsResult> GetStatsAsync(StatsPeriod period)
    {
        var since = SinceUtcIso(period);

        return await Task.Run(async () =>
        {
            await using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder(_conn.ConnectionString)
                {
                    Mode = SqliteOpenMode.ReadOnly
                }.ConnectionString);
            await conn.OpenAsync();

            var rows = (await conn.QueryAsync<(string Title, string Artist, long DurationMs)>("""
                SELECT track_title, track_artist, duration_ms
                FROM play_log
                WHERE played_at >= @since
                """, new { since })).ToList();

            return ComputeStats(rows);
        });
    }

    /// <summary>Aggregates play_log rows into a StatsResult (pure function — easy to test).</summary>
    private static StatsResult ComputeStats(List<(string Title, string Artist, long DurationMs)> rows)
    {
        var plays = rows.Count;
        var unique = rows.Select(r => (r.Title, r.Artist)).Distinct().Count();
        var hours = Math.Round(rows.Sum(r => r.DurationMs) / 3600000.0, 1);

        var artistPlays = new Dictionary<string, (string Display, int Plays)>();
        foreach (var row in rows)
        {
            foreach (var name in ArtistHelper.Split(row.Artist))
            {
                var key = ArtistHelper.Key(name);
                if (key.Length == 0) continue;
                var cur = artistPlays.GetValueOrDefault(key);
                artistPlays[key] = (
                    cur.Display is { Length: > 0 } ? cur.Display : name,
                    cur.Plays + 1);
            }
        }

        var trackPlays = new Dictionary<(string Title, string Artist), int>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Title)) continue;
            var key = (row.Title, row.Artist);
            trackPlays[key] = trackPlays.GetValueOrDefault(key) + 1;
        }

        var topArtists = artistPlays
            .OrderByDescending(p => p.Value.Plays).ThenBy(p => p.Value.Display)
            .Take(10)
            .Select(p => (p.Value.Display, p.Value.Plays))
            .ToList();

        var topTracks = trackPlays
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key.Title)
            .Take(10)
            .Select(p => (p.Key.Title, p.Key.Artist, Plays: p.Value))
            .ToList();

        var replayed = topTracks.FirstOrDefault();

        return new StatsResult
        {
            UniqueTracks = unique,
            Plays = plays,
            Hours = hours,
            TopArtists = topArtists,
            TopTracks = topTracks,
            MostReplayed = replayed.Title != null ? (replayed.Title, replayed.Artist, replayed.Plays) : null
        };
    }
}