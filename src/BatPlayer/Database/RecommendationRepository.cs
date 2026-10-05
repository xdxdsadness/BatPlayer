using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Helpers;

namespace BatPlayer.Database;

/// <summary>Кандидат волны: снимок метаданных трека из ответа /tracks/{id}/similar.
/// Отдельный класс от YmTrackDto — хранится в wave_similar и не зависит от раскладки API.</summary>
public sealed class WaveCandidateRow
{
    public string YmId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    /// <summary>Шаблон обложки с "%%" вместо размера (как в API); '' — обложки нет.</summary>
    public string CoverUri { get; set; } = string.Empty;
    public bool Available { get; set; } = true;
}

/// <summary>Сид волны: ym_id трека, по которому запрашивается /similar.</summary>
public sealed class WaveSeedRow
{
    public string YmId { get; set; } = string.Empty;
    /// <summary>Источник сида ("yandex" — лайк ЯМ, "vk", "soundcloud", "local"): только
    /// для логов/диагностики; на подбор кандидатов не влияет.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Запись журнала wave_suggested: трек уже предлагался волной.</summary>
public sealed class WaveSuggestedRow
{
    public string YmId { get; set; } = string.Empty;
    public string ArtistKey { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public DateTime SuggestedAt { get; set; }
    public bool Played { get; set; }
}

/// <summary>
/// Хранилище «Моей волны»: кэш ответов /similar (wave_similar), сопоставление сидов
/// VK/SC/локальных треков с ym_id через /search (wave_seed_map) и журнал предложений
/// (wave_suggested). Таблицы создаёт миграция v5 (DatabaseContext).
///
/// ОТДЕЛЬНЫЕ соединения на операцию (как HistoryService.GetStatsAsync), а не общее
/// из ServiceContainer: генерация волны выполняется в фоне (Task.Run), и запросы
/// на общем соединении конфликтовали бы с запросами страниц/плеера — Microsoft.Data.Sqlite
/// не допускает параллельных команд на одном соединении. WAL (см. InitializeAsync)
/// обеспечивает конкурентное чтение/запись на уровне файла.
/// </summary>
public sealed class RecommendationRepository
{
    private readonly string _connectionString;

    /// <param name="dbPath">Путь файла БД; тесты передают временный.</param>
    public RecommendationRepository(string dbPath)
        => _connectionString = $"Data Source={dbPath};Cache=Shared;Pooling=True;";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ======================== Кэш похожести (wave_similar) ========================

    /// <summary>Сиды, кэш которых отсутствует или старше maxAge — им нужен запрос /similar.</summary>
    public async Task<List<string>> GetStaleSeedsAsync(IEnumerable<string> seedYmIds, TimeSpan maxAge)
    {
        var ids = seedYmIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (ids.Count == 0) return new List<string>();

        await using var conn = Open();
        var rows = await conn.QueryAsync<string>(
            """
            SELECT seed_ym_id FROM wave_similar
            WHERE seed_ym_id IN @ids
            GROUP BY seed_ym_id
            HAVING MAX(fetched_at) >= @threshold
            """,
            new { ids, threshold = DateTime.UtcNow.Subtract(maxAge).ToString("o") });

        var fresh = rows.ToHashSet(StringComparer.Ordinal);
        return ids.Where(id => !fresh.Contains(id)).ToList();
    }

    /// <summary>Свежие кандидаты из кэша для перечисленных сидов (по одному срезу на сид).</summary>
    public async Task<Dictionary<string, List<WaveCandidateRow>>> GetSimilarAsync(
        IEnumerable<string> seedYmIds, TimeSpan maxAge)
    {
        var ids = seedYmIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        var result = new Dictionary<string, List<WaveCandidateRow>>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;

        await using var conn = Open();
        var threshold = DateTime.UtcNow.Subtract(maxAge).ToString("o");
        var rows = await conn.QueryAsync<(string SeedYmId, string YmId, string Title, string Artist,
            long DurationMs, string CoverUri, bool Available)>(
            """
            SELECT seed_ym_id, ym_id, title, artist, duration_ms, cover_uri, available
            FROM wave_similar
            WHERE seed_ym_id IN @ids AND fetched_at >= @threshold
            ORDER BY seed_ym_id, rank
            """,
            new { ids, threshold });

        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.SeedYmId, out var list))
                result[row.SeedYmId] = list = new List<WaveCandidateRow>();
            list.Add(new WaveCandidateRow
            {
                YmId = row.YmId,
                Title = row.Title,
                Artist = row.Artist,
                DurationMs = row.DurationMs,
                CoverUri = row.CoverUri,
                Available = row.Available
            });
        }
        return result;
    }

    /// <summary>Заменить кэш сида свежим ответом /similar (полная перезапись среза).</summary>
    public async Task ReplaceSimilarAsync(string seedYmId, IReadOnlyList<WaveCandidateRow> candidates)
    {
        var fetchedAt = DateTime.UtcNow.ToString("o");
        await using var conn = Open();
        await using var tx = (await conn.BeginTransactionAsync());
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM wave_similar WHERE seed_ym_id = @seedYmId",
                new { seedYmId }, tx);

            if (candidates.Count > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO wave_similar(seed_ym_id, ym_id, rank, title, artist, duration_ms, cover_uri, available, fetched_at)
                    VALUES(@SeedYmId, @YmId, @Rank, @Title, @Artist, @DurationMs, @CoverUri, @Available, @FetchedAt)
                    ON CONFLICT(seed_ym_id, ym_id) DO UPDATE SET
                        rank = @Rank, title = @Title, artist = @Artist,
                        duration_ms = @DurationMs, cover_uri = @CoverUri,
                        available = @Available, fetched_at = @FetchedAt
                    """,
                    candidates.Select((c, i) => new
                    {
                        SeedYmId = seedYmId,
                        c.YmId,
                        Rank = i,
                        c.Title,
                        c.Artist,
                        c.DurationMs,
                        c.CoverUri,
                        c.Available,
                        FetchedAt = fetchedAt
                    }), tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ===================== Сопоставление сидов (wave_seed_map) =====================

    /// <summary>
    /// Кэш сопоставления сида с ym_id: null — поиск ещё не выполнялся; YmId='' —
    /// выполнялся и матча не нашёл (негативный кэш; ResolvedAt регулирует TTL повтора).
    /// </summary>
    public async Task<(string YmId, DateTime ResolvedAt)?> GetSeedMapAsync(string source, string seedId)
    {
        await using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync<(string YmId, string ResolvedAt)>(
            "SELECT ym_id, resolved_at FROM wave_seed_map WHERE source = @source AND seed_id = @seedId",
            new { source, seedId });
        if (row == default) return null;

        var resolvedAt = DateTime.TryParse(row.ResolvedAt, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;
        return (row.YmId, resolvedAt);
    }

    public async Task SaveSeedYmIdAsync(string source, string seedId, string ymId)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO wave_seed_map(source, seed_id, ym_id, resolved_at)
            VALUES(@source, @seedId, @ymId, @now)
            ON CONFLICT(source, seed_id) DO UPDATE SET
                ym_id = @ymId, resolved_at = @now
            """,
            new { source, seedId, ymId, now = DateTime.UtcNow.ToString("o") });
    }

    // ======================= Журнал предложений (wave_suggested) =======================

    public async Task<List<WaveSuggestedRow>> GetSuggestedAsync()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<(string YmId, string ArtistKey, string TitleKey,
            string SuggestedAt, long Played)>(
            "SELECT ym_id, artist_key, title_key, suggested_at, played FROM wave_suggested");
        return rows.Select(r => new WaveSuggestedRow
        {
            YmId = r.YmId,
            ArtistKey = r.ArtistKey,
            TitleKey = r.TitleKey,
            SuggestedAt = DateTime.TryParse(r.SuggestedAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow,
            Played = r.Played != 0
        }).ToList();
    }

    /// <summary>Пометить треки предложенными (upsert; повторное предложение обновляет дату
    /// и сбрасывает played — свежесть предложения важнее прошлой статистики).</summary>
    public async Task MarkSuggestedAsync(IEnumerable<(string YmId, string Artist, string Title)> tracks)
    {
        var rows = tracks
            .Where(t => !string.IsNullOrEmpty(t.YmId))
            .Select(t => new
            {
                YmId = t.YmId,
                // Семейный ключ (фиты отрезаны) — совпадает с ключами RankCandidates:
                // демоушн и дисконт прослушек должны находить этот трек в журнале.
                ArtistKey = ArtistHelper.Key(ArtistHelper.StripFeatures(
                    ArtistHelper.Split(t.Artist).FirstOrDefault() ?? string.Empty)),
                TitleKey = MatchHelper.Normalize(t.Title),
                Now = DateTime.UtcNow.ToString("o")
            })
            .ToList();
        if (rows.Count == 0) return;

        await using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO wave_suggested(ym_id, artist_key, title_key, suggested_at, played)
            VALUES(@YmId, @ArtistKey, @TitleKey, @Now, 0)
            ON CONFLICT(ym_id) DO UPDATE SET
                artist_key = @ArtistKey, title_key = @TitleKey,
                suggested_at = @Now, played = 0
            """, rows);
    }

    /// <summary>Отметить прослушанные среди предложенных (вызывается перед генерацией:
    /// трек, который пользователь реально слушал после предложения, не исключается).</summary>
    public async Task MarkPlayedAsync(IReadOnlyCollection<string> ymIds)
    {
        var ids = ymIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (ids.Count == 0) return;

        await using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE wave_suggested SET played = 1 WHERE ym_id IN @ids",
            new { ids });
    }

    /// <summary>Удалить журнал старше months — таблица не должна расти вечно.</summary>
    public async Task PruneAsync(int months)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            "DELETE FROM wave_suggested WHERE suggested_at < @cutoff AND played = 0",
            new { cutoff = DateTime.UtcNow.AddMonths(-months).ToString("o") });
    }

    // ======================= Срезы play_log (сигналы вкуса) =======================

    /// <summary>Прослушки за период: (исполнитель, название, played_at) — сырьё для
    /// аффинности по исполнителям и фильтра «недавно играло». Читается из play_log,
    /// который плеер уже пишет для всех источников, включая платформенные треки.</summary>
    public async Task<List<(string Artist, string Title, DateTime PlayedAt)>> GetRecentPlaysAsync(int days)
    {
        await using var conn = Open();
        var since = DateTime.UtcNow.AddDays(-days).ToString("o");
        var rows = await conn.QueryAsync<(string Artist, string Title, string PlayedAt)>(
            """
            SELECT track_artist, track_title, played_at FROM play_log
            WHERE played_at >= @since
            ORDER BY played_at
            """,
            new { since });
        return rows.Select(r => (r.Artist, r.Title,
                DateTime.TryParse(r.PlayedAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow))
            .ToList();
    }
}
