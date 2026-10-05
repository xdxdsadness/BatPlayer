using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Дисковый кэш аудио-стримов SoundCloud: качает аудио (mp3 progressive / склейка
/// HLS-сегментов mp3 или AAC) в локальный файл через общий сетевой слой
/// <see cref="SoundCloudHttp"/> (тот же direct ↔ прокси фолбэк, что и у API-запросов)
/// и отдаёт путь к файлу — плеер всегда играет локальный файл.
///
/// Зачем: MediaFoundationReader в AudioEngine не умеет SOCKS-прокси пользователя и открывает
/// http-URL синхронно на UI-потоке (секунды блокировки). Локальный файл снимает обе проблемы,
/// а заодно даёт естественный офлайн-кэш: повторный клик играет без сети.
///
/// Имя файла — {scId}.mp3 (mp3) или {scId}.m4a (AAC, качество веб-плеера);
/// каталог по умолчанию %LOCALAPPDATA%/BatPlayer/sc_cache/.
/// Скачивание идёт в .part и атомарно переезжает в финальное имя (обрезанный файл не попадёт
/// в кэш как валидный). Параллельные запросы одного трека сериализуются на scId.
/// </summary>
public sealed class SoundCloudStreamCache
{
    /// <summary>Лимит каталога кэша: при превышении чистятся самые старые файлы.</summary>
    public const long DefaultMaxCacheBytes = 500L * 1024 * 1024;

    /// <summary>До какого размера чистим каталог (гистерезис, чтобы не чистить на каждый трек).</summary>
    public const long DefaultTargetCacheBytes = 300L * 1024 * 1024;

    /// <summary>Расширение mp3-кэша (progressive / склейка HLS mp3-сегментов).</summary>
    private const string Extension = ".mp3";

    /// <summary>Расширение AAC-кэша (HLS audio/mp4 — то же качество, что у веб-плеера).
    /// public: резолв потока сохраняет AAC именно с ним (AudioEngine открывает .m4a через Media Foundation).</summary>
    public const string AacExtension = ".m4a";

    /// <summary>Все расширения, которые живут в каталоге кэша (для метлы и лимита).</summary>
    private static readonly string[] KnownExtensions = { Extension, AacExtension };

    private readonly string _cacheDir;
    private readonly long _maxCacheBytes;
    private readonly long _targetCacheBytes;

    /// <summary>Замок на scId: одновременная загрузка одного трека даёт один файл.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Сколько живёт невостребованный mp3 в кэше: разгрузка диска/памяти —
    /// давно не игравшиеся и не префетченные файлы выгружаются, повторный клик
    /// просто перекачивает. Файл играющего трека удалён не будет: занят плеером
    /// (delete падает — пропускаем) либо уже целиком в памяти движка.</summary>
    public static readonly TimeSpan IdleEntryTtl = TimeSpan.FromMinutes(5);

    private readonly System.Threading.Timer? _idleEvictor;

    /// <param name="cacheDir">Каталог кэша; null — %LOCALAPPDATA%/BatPlayer/sc_cache (тесты передают временный).</param>
    public SoundCloudStreamCache(string? cacheDir = null,
                                 long maxCacheBytes = DefaultMaxCacheBytes,
                                 long targetCacheBytes = DefaultTargetCacheBytes,
                                 bool enableIdleEviction = true)
    {
        _cacheDir = cacheDir ?? Path.Combine(App.AppDataDir, "sc_cache");
        _maxCacheBytes = maxCacheBytes;
        _targetCacheBytes = targetCacheBytes;
        // Метла: раз в минуту выгружаем mp3, к которым >5 минут никто не обращался.
        // Юнит-тесты передают временный каталог с enableIdleEviction=false, чтобы не
        // гонять таймер и не терять фикс fstures посреди теста.
        _idleEvictor = enableIdleEviction
            ? new System.Threading.Timer(
                _ => EvictIdle(IdleEntryTtl), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))
            : null;
    }

    /// <summary>
    /// Выгрузка «протухших» записей: mp3-файлы, время записи которых старше
    /// <paramref name="idle"/>. Обращение к кэш-файлу (GetStreamFileAsync) обновляет
    /// его время — играемый/переигрываемый трек не выгружается. Ошибки удаления
    /// (файл занят плеером) — тихо пропускаются.
    /// </summary>
    public void EvictIdle(TimeSpan idle)
    {
        try
        {
            var thresholdUtc = DateTime.UtcNow - idle;
            foreach (var file in CacheFiles())
            {
                if (file.LastWriteTimeUtc >= thresholdUtc) continue;
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Stream cache idle eviction failed");
        }
    }

    /// <summary>Каталог кэша (для диагностики/тестов).</summary>
    public string CacheDir => _cacheDir;

    /// <summary>mp3/AAC трека уже в кэше (файл существует и непустой) — можно играть офлайн без сети.</summary>
    public bool IsTrackCached(string scId) => GetExistingCachePath(scId) != null;

    /// <summary>
    /// Существующий кэш-файл трека или null. При наличии обоих вариантов предпочитаем
    /// .m4a (AAC 160 kbps) — он качается после .mp3 и звучит лучше.
    /// </summary>
    public string? GetExistingCachePath(string scId)
    {
        var aac = GetCacheFilePath(scId, AacExtension);
        if (IsCached(aac)) return aac;
        var mp3 = GetCacheFilePath(scId);
        return IsCached(mp3) ? mp3 : null;
    }

    /// <summary>
    /// Путь кэш-файла для трека. scId — id трека из API (цифры); на всякий случай
    /// всё, кроме [A-Za-z0-9_-], заменяется на '_' — чтобы id из БД не вывел путь наружу каталога.
    /// </summary>
    public string GetCacheFilePath(string scId, string extension = Extension)
    {
        var safe = new string((scId ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
            .ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_cacheDir, safe + extension);
    }

    /// <summary>
    /// Файл трека в кэше: уже скачанный — сразу путь; иначе скачивание стрима и путь к нему.
    /// Бросает <see cref="SoundCloudApiException"/> на HTTP-неуспехе (файл .part удаляется).
    /// </summary>
    public async Task<string> GetStreamFileAsync(string streamUrl, string scId, CancellationToken ct)
    {
        var finalPath = GetCacheFilePath(scId);
        if (IsCached(finalPath))
        {
            Touch(finalPath);
            return finalPath;
        }

        var gate = _gates.GetOrAdd(scId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Повторная проверка: пока ждали замок, трек мог скачать другой запрос.
            if (IsCached(finalPath)) { Touch(finalPath); return finalPath; }

            // Чистим каталог перед записью (по спеке — «перед каждой записью»), затем качаем.
            EnforceLimit();

            return await DownloadAsync(streamUrl, scId, finalPath, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Готовые байты (склейка HLS-сегментов) в кэш: .part → атомарная публикация.
    /// Уже скачанный трек — сразу путь, байты игнорируются.
    /// </summary>
    public async Task<string> SaveTrackBytesAsync(byte[] data, string scId, CancellationToken ct,
        string extension = Extension)
    {
        var finalPath = GetCacheFilePath(scId, extension);
        if (IsCached(finalPath))
        {
            Touch(finalPath);
            return finalPath;
        }

        var gate = _gates.GetOrAdd(scId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (IsCached(finalPath)) { Touch(finalPath); return finalPath; }

            EnforceLimit();

            Directory.CreateDirectory(_cacheDir);
            var tempPath = finalPath + ".part";
            await File.WriteAllBytesAsync(tempPath, data, ct);
            File.Move(tempPath, finalPath, overwrite: true);
            Logger.Info($"SoundCloud cache written from HLS: {scId} {data.Length / 1024} KB");
            return finalPath;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud HLS cache write failed (scId {scId})");
            TryDelete(finalPath + ".part");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Уже скачан и непустой (нулевой файл — след оборванной записи, качаем заново).</summary>
    private static bool IsCached(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    private async Task<string> DownloadAsync(string streamUrl, string scId, string finalPath, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDir);
        var tempPath = finalPath + ".part";
        var sw = Stopwatch.StartNew();

        try
        {
            var (status, stream, owner) = await SoundCloudHttp.SendStreamAsync(
                () => new HttpRequestMessage(HttpMethod.Get, streamUrl), ct);

            using (owner)
            {
                if (status != 200 || stream == null)
                {
                    Logger.Error($"SoundCloud stream download failed with HTTP {status} (scId {scId})");
                    throw new SoundCloudApiException($"stream download failed with HTTP {status}", status);
                }

                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await stream.CopyToAsync(file, ct);
            }

            // Атомарная публикация: до Move в кэше лежит только .part.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud stream download failed (scId {scId})");
            TryDelete(tempPath);
            throw;
        }

        var sizeKb = new FileInfo(finalPath).Length / 1024;
        Logger.Info($"SoundCloud stream cached: {scId} {sizeKb} KB {sw.ElapsedMilliseconds}ms");
        return finalPath;
    }

    /// <summary>
    /// Держит каталог в лимите: если суммарный размер mp3-файлов больше
    /// <see cref="_maxCacheBytes"/> — удаляет самые старые (по LastWriteTime) до
    /// <see cref="_targetCacheBytes"/>. .part-файлы не трогаем: их пишут активные загрузки.
    /// </summary>
    public void EnforceLimit()
    {
        try
        {
            var files = CacheFiles();
            var total = files.Sum(f => f.Length);
            if (total <= _maxCacheBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= _targetCacheBytes) break;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                    Logger.Info($"SoundCloud cache evicted: {file.Name} ({size / 1024} KB)");
                }
                catch (Exception ex)
                {
                    // Файл занят (играется) или уже удалён — пропускаем, лимит не критичен.
                    Logger.Error(ex, $"SoundCloud cache eviction failed ({file.Name})");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud cache limit enforcement failed");
        }
    }

    private List<FileInfo> CacheFiles()
    {
        var dir = new DirectoryInfo(_cacheDir);
        if (!dir.Exists) return new List<FileInfo>();
        return dir.GetFiles()
            .Where(f => KnownExtensions.Any(ext => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud stream temp cleanup failed ({Path.GetFileName(path)})");
        }
    }

    /// <summary>Обновить время файла кэша (метка обращения — от неё тикает TTL выгрузки).</summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // нет файла / занят — не критично: метла просто сносит его раньше
        }
    }
}
