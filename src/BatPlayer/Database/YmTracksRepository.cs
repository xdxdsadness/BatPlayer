using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BatPlayer.Database;

/// <summary>
/// Одна запись трека Яндекс Музыки: только метаданные (скачивание аудио не предусмотрено).
/// ym_id — числовой id трека в API Яндекс Музыки, храним TEXT. Временные mp3-ссылки из
/// download-info в БД НЕ пишутся: они живут недолго и разрешаются заново в памяти
/// при стриминге (YmService.GetStreamUrlAsync). Available — флаг тарифной доступности
/// (поле available API): недоступные треки видны в каталоге, но приглушены.
/// </summary>
public sealed class YmTrackRow
{
    public string YmId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ArtworkUrl { get; set; } = string.Empty;

    /// <summary>Путь обложки в локальном кэше (artworks_cache/ym_{ym_id}.jpg); null — ещё не скачана.</summary>
    public string? ArtworkLocalPath { get; set; }

    public bool Available { get; set; } = true;

    /// <summary>Время лайка из API (ISO 8601): каталог сортируется по нему — «свежие
    /// лайки сверху». null у строк, синкнутых до появления колонки.</summary>
    public string? LikedAt { get; set; }

    public string SyncedAt { get; set; } = string.Empty;
}

/// <summary>
/// Репозиторий таблицы ym_tracks. Upsert по ym_id, выдача по времени лайка (свежие
/// сверху — как в Яндекс Музыке); строки без времени лайка (старые синки) — в конце.
/// </summary>
public sealed class YmTracksRepository
{
    private readonly SqliteConnection _conn;

    public YmTracksRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>Пакетный upsert: повторная синхронизация не дублирует записи
    /// (artwork_local_path при пере-синке сохраняется — не трогаем; liked_at
    /// обновляется временем лайка из API).</summary>
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

    /// <summary>Все треки Яндекс Музыки по времени лайка — «свежие сверху», как в
    /// Яндекс Музыке. Строки без liked_at (синкнутые до появления колонки) — в конце.</summary>
    public async Task<List<YmTrackRow>> GetAllAsync()
    {
        var rows = await _conn.QueryAsync<YmTrackRow>(
            "SELECT * FROM ym_tracks ORDER BY liked_at DESC, rowid ASC");
        return rows.AsList();
    }

    /// <summary>Трек по ym_id или null — резолверу плеера нужна строка для получения стрима.</summary>
    public async Task<YmTrackRow?> GetByYmIdAsync(string ymId)
        => await _conn.QueryFirstOrDefaultAsync<YmTrackRow>(
            "SELECT * FROM ym_tracks WHERE ym_id = @ymId", new { ymId });

    public async Task<int> CountAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ym_tracks");

    /// <summary>Сколько строк ещё без liked_at: после миграции v7 время лайка появляется
    /// только синком — по этому счётчику страница делает догоняющий синк один раз.</summary>
    public async Task<int> CountWithoutLikedAtAsync()
        => await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ym_tracks WHERE liked_at IS NULL");

    public async Task ClearAllAsync()
        => await _conn.ExecuteAsync("DELETE FROM ym_tracks");

    /// <summary>
    /// Удалить треки, которых нет среди текущих лайков: снятые с лайка записи
    /// исчезают со страницы по синку, а не копятся вечно. Возвращает число удалённых.
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
    /// Путь локальной обложки для трека. Отдельно от UpsertBatch: пере-синк метаданных
    /// не должен сбрасывать уже скачанные пути.
    /// </summary>
    public async Task SetArtworkLocalPathAsync(string ymId, string? path)
        => await _conn.ExecuteAsync(
            "UPDATE ym_tracks SET artwork_local_path = @path WHERE ym_id = @ymId",
            new { path, ymId });
}
