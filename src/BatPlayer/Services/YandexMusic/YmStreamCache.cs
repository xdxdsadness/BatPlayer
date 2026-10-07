using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Disk cache for Yandex Music mp3 streams: a wrapper over
/// <see cref="Services.SoundCloud.SoundCloudStreamCache"/> with its own directory
/// (%LOCALAPPDATA%/BatPlayer/ym_cache). The internal mechanics (download to .part +
/// atomic publish, serializing parallel requests for one track, directory limit with
/// eviction of the oldest files) are fully reused; only the cache directory changes —
/// ym_id (digits) works directly as a file name. A separate type exists so it can live
/// next to the SC/VK caches in DI rather than replace them.
///
/// Downloads go through the direct layer <see cref="SoundCloudHttp"/> (same direct →
/// system proxy fallback as YM requests; the UA is the common browser one — the Yandex
/// CDN does not care).
///
/// File downloads are NOT offered to the user: the cache exists only for streaming
/// (a repeated click plays offline without network).
/// </summary>
public sealed class YmStreamCache
{
    private readonly SoundCloud.SoundCloudStreamCache _inner;

    /// <param name="cacheDir">Cache directory; null — %LOCALAPPDATA%/BatPlayer/ym_cache (tests pass a temp one).</param>
    public YmStreamCache(string? cacheDir = null)
        => _inner = new SoundCloud.SoundCloudStreamCache(cacheDir ?? Path.Combine(App.AppDataDir, "ym_cache"));

    /// <summary>Cache directory (for diagnostics/tests).</summary>
    public string CacheDir => _inner.CacheDir;

    /// <summary>Track mp3 already cached (file exists and is non-empty) — can play offline.</summary>
    public bool IsTrackCached(string ymId) => _inner.IsTrackCached(ymId);

    /// <summary>Cache file path for a track: ym_cache/{ym_id}.mp3.</summary>
    public string GetCacheFilePath(string ymId) => _inner.GetCacheFilePath(ymId);

    /// <summary>
    /// Cached track file: already downloaded — returns the path immediately; otherwise
    /// downloads the stream via the temporary URL and returns its path. Throws on HTTP
    /// failure (the .part file is removed).
    /// </summary>
    public Task<string> GetStreamFileAsync(string streamUrl, string ymId, CancellationToken ct)
        => _inner.GetStreamFileAsync(streamUrl, ymId, ct);
}
