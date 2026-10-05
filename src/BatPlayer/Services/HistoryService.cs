using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Helpers;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>Итоговый срез статистики за период.</summary>
public sealed class StatsResult
{
    /// <summary>Сколько РАЗНЫХ треков было прослушано.</summary>
    public int UniqueTracks { get; init; }

    /// <summary>Сколько всего прослушиваний (включая повторы) за период.</summary>
    public int Plays { get; init; }

    /// <summary>Суммарное время прослушанной музыки, часы.</summary>
    public double Hours { get; init; }

    /// <summary>Топ-5 исполнителей по числу прослушиваний.</summary>
    public List<(string Artist, int Plays)> TopArtists { get; init; } = new();

    /// <summary>Топ-10 треков по числу прослушиваний.</summary>
    public List<(string Title, string Artist, int Plays)> TopTracks { get; init; } = new();

    /// <summary>Трек, который чаще всего ставили на повтор (null — нечего показывать).</summary>
    public (string Title, string Artist, int Plays)? MostReplayed { get; init; }
}

/// <summary>Период статистики.</summary>
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

    /// <summary>Записана новая прослушка: открытая страница «Недавно прослушанные»
    /// перечитывает список (новый трек наверх, 50-й уходит) без повторного захода.</summary>
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
    /// Регистрация прослушки: для треков библиотеки обновляются агрегаты (history/tracks),
    /// для ВСЕХ треков пишется строка в play_log со СНИМКОМ трека (+platform_id и
    /// artwork_path: прослушки вне справочников — рекомендации «Моей волны», которых нет
    /// среди лайков — восстанавливаются на «Недавно прослушанных» по снимку). Платформенные
    /// runtime-карточки (SoundCloud/VK/Яндекс Музыка) имеют отрицательный Id и в таблице
    /// tracks отсутствуют — вставка в history с FK падала (SQLite Error 19) и прерывала
    /// запись прослушки, поэтому для них агрегаты пропускаются.
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

    // ============================ Статистика ============================

    private static string SinceUtcIso(StatsPeriod period) => period switch
    {
        StatsPeriod.Day => DateTime.UtcNow.AddDays(-1).ToString("o"),
        StatsPeriod.Week => DateTime.UtcNow.AddDays(-7).ToString("o"),
        StatsPeriod.Month => DateTime.UtcNow.AddDays(-30).ToString("o"),
        StatsPeriod.Year => DateTime.UtcNow.AddDays(-365).ToString("o"),
        _ => "0001-01-01T00:00:00.0000000"
    };

    /// <summary>
    /// Полный срез статистики за период. Агрегация в C# (не SQL), чтобы соавторов
    /// разбирать по ArtistHelper.Split: строка "August, TikoTheCEO" даёт прослушку
    /// ОБОИМ артистам, а регистронезависимый ключ склеивает "Kai Angel"/"kai angel"
    /// и треки разных платформ.
    /// Вся работа — на фоновом потоке через ОТДЕЛЬНОЕ read-only соединение:
    /// async-методы Microsoft.Data.Sqlite на самом деле синхронны, и запрос +
    /// агрегация play_log на общем UI-соединении подвешивали интерфейс на время
    /// пересчёта (в т.ч. мешали тикам позиции плеера). Отдельное соединение ещё
    /// и не спорит с записью прослушек на общем.
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

    /// <summary>Агрегация строк play_log в StatsResult (чистая функция — удобно тестировать).</summary>
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