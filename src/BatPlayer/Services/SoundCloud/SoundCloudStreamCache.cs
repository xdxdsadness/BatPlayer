using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Disk cache of SoundCloud audio streams: downloads audio (mp3 progressive / stitched HLS
/// mp3 or AAC segments) into a local file via the shared <see cref="SoundCloudHttp"/> network
/// layer (same direct ↔ proxy fallback as API requests) and returns the file path — the
/// player always plays a local file.
///
/// Why: AudioEngine's MediaFoundationReader can't use the user's SOCKS proxy and opens http
/// URLs synchronously on the UI thread (seconds of blocking). A local file removes both
/// problems and doubles as a natural offline cache: a repeated click plays without network.
///
/// File name is {scId}.mp3 (mp3) or {scId}.m4a (AAC, web-player quality); default directory
/// %LOCALAPPDATA%/BatPlayer/sc_cache/. Downloads go to .part and are atomically renamed to
/// the final name (a truncated file never lands in the cache as valid). Parallel requests
/// for one track serialize on scId.
/// </summary>
public sealed class SoundCloudStreamCache
{
    /// <summary>Cache directory limit: oldest files are cleaned when exceeded.</summary>
    public const long DefaultMaxCacheBytes = 500L * 1024 * 1024;

    /// <summary>Cleanup target size (hysteresis, so we don't clean on every track).</summary>
    public const long DefaultTargetCacheBytes = 300L * 1024 * 1024;

    /// <summary>mp3 cache extension (progressive / stitched HLS mp3 segments).</summary>
    private const string Extension = ".mp3";

    /// <summary>AAC cache extension (HLS audio/mp4 — same quality as the web player).
    /// public: stream resolve saves AAC with exactly it (AudioEngine opens .m4a via Media Foundation).</summary>
    public const string AacExtension = ".m4a";

    /// <summary>All extensions living in the cache directory (for eviction and the limit).</summary>
    private static readonly string[] KnownExtensions = { Extension, AacExtension };

    private readonly string _cacheDir;
    private readonly long _maxCacheBytes;
    private readonly long _targetCacheBytes;

    /// <summary>Per-scId gate: concurrent downloads of one track produce a single file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>How long an untouched mp3 lives in the cache: relieves disk/memory —
    /// files not played or prefetched for a while are evicted; a repeated click simply
    /// re-downloads. The playing track's file is never removed: held by the player
    /// (delete fails — skipped) or already fully in engine memory.</summary>
    public static readonly TimeSpan IdleEntryTtl = TimeSpan.FromMinutes(5);

    private readonly System.Threading.Timer? _idleEvictor;

    /// <param name="cacheDir">Cache directory; null — %LOCALAPPDATA%/BatPlayer/sc_cache (tests pass a temp one).</param>
    public SoundCloudStreamCache(string? cacheDir = null,
                                 long maxCacheBytes = DefaultMaxCacheBytes,
                                 long targetCacheBytes = DefaultTargetCacheBytes,
                                 bool enableIdleEviction = true)
    {
        _cacheDir = cacheDir ?? Path.Combine(App.AppDataDir, "sc_cache");
        _maxCacheBytes = maxCacheBytes;
        _targetCacheBytes = targetCacheBytes;
        // Evictor: once a minute, evict mp3s untouched for over 5 minutes. Unit tests pass
        // a temp dir with enableIdleEviction=false, to avoid the timer and losing fixtures mid-test.
        _idleEvictor = enableIdleEviction
            ? new System.Threading.Timer(
                _ => EvictIdle(IdleEntryTtl), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))
            : null;
    }

    /// <summary>
    /// Evicts stale entries: mp3 files whose write time is older than <paramref name="idle"/>.
    /// Touching a cache file (GetStreamFileAsync) refreshes its time — playing/replayed tracks
    /// are not evicted. Delete errors (file held by the player) are silently skipped.
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

    /// <summary>Cache directory (for diagnostics/tests).</summary>
    public string CacheDir => _cacheDir;

    /// <summary>The track's mp3/AAC is already cached (file exists and non-empty) — playable offline without network.</summary>
    public bool IsTrackCached(string scId) => GetExistingCachePath(scId) != null;

    /// <summary>
    /// Existing cache file for a track, or null. When both variants exist, .m4a (AAC 160 kbps)
    /// is preferred — it is downloaded after the .mp3 and sounds better.
    /// </summary>
    public string? GetExistingCachePath(string scId)
    {
        var aac = GetCacheFilePath(scId, AacExtension);
        if (IsCached(aac)) return aac;
        var mp3 = GetCacheFilePath(scId);
        return IsCached(mp3) ? mp3 : null;
    }

    /// <summary>
    /// Cache file path for a track. scId is the track id from the API (digits); as a guard,
    /// anything outside [A-Za-z0-9_-] is replaced with '_' — a DB id must not escape the directory.
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
    /// Track file in cache: already downloaded — path right away; otherwise downloads the
    /// stream and returns its path. Throws <see cref="SoundCloudApiException"/> on HTTP
    /// failure (the .part file is removed).
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
            // Double-check: while waiting on the gate, another request may have downloaded the track.
            if (IsCached(finalPath)) { Touch(finalPath); return finalPath; }

            // Enforce the limit before writing (per spec — "before each write"), then download.
            EnforceLimit();

            return await DownloadAsync(streamUrl, scId, finalPath, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes ready bytes (stitched HLS segments) to the cache: .part → atomic publish.
    /// Already-downloaded track — path right away, bytes ignored.
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

    /// <summary>Downloaded and non-empty (a zero-length file is a remnant of an interrupted write; re-download).</summary>
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

            // Atomic publish: until the Move, only .part sits in the cache.
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
    /// Keeps the directory within its limit: if total mp3 size exceeds
    /// <see cref="_maxCacheBytes"/> — deletes the oldest files (by LastWriteTime) down to
    /// <see cref="_targetCacheBytes"/>. .part files are untouched: active downloads write them.
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
                    // File in use (playing) or already gone — skip; the limit is not critical.
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

    /// <summary>Refreshes the cache file's time (access mark — the eviction TTL counts from it).</summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // missing/in-use — not critical: the evictor just removes it sooner
        }
    }
}
