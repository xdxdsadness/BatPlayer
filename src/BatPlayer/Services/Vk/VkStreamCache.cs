using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Disk cache for VK mp3 streams: a wrapper over <see cref="Services.SoundCloud.SoundCloudStreamCache"/>
/// with its own directory (%LOCALAPPDATA%/BatPlayer/vk_cache). The internal mechanics
/// (download to .part + atomic publish, serializing parallel requests for one track,
/// directory limit with eviction of the oldest files) are fully reused; only the cache
/// directory changes — vk_id ("{owner_id}_{id}", digits and '_' only) works directly
/// as a file name. A separate type exists so it can live next to the SC cache in DI
/// rather than replace it.
///
/// Downloads go through the shared SoundCloudHttp network layer (same direct → system
/// proxy fallback as VK requests; the UA is the common browser one — the VK CDN does not care).
///
/// File downloads are NOT offered to the user: the cache exists only for streaming
/// (a repeated click plays offline without network).
/// </summary>
public sealed class VkStreamCache
{
    private readonly SoundCloud.SoundCloudStreamCache _inner;

    /// <param name="cacheDir">Cache directory; null — %LOCALAPPDATA%/BatPlayer/vk_cache (tests pass a temp one).</param>
    public VkStreamCache(string? cacheDir = null)
        => _inner = new SoundCloud.SoundCloudStreamCache(cacheDir ?? Path.Combine(App.AppDataDir, "vk_cache"));

    /// <summary>Cache directory (for diagnostics/tests).</summary>
    public string CacheDir => _inner.CacheDir;

    /// <summary>Track mp3 already cached (file exists and is non-empty) — can play offline.</summary>
    public bool IsTrackCached(string vkId) => _inner.IsTrackCached(vkId);

    /// <summary>Cache file path for a track: vk_cache/{vk_id}.mp3 (vk_id = "{owner_id}_{id}").</summary>
    public string GetCacheFilePath(string vkId) => _inner.GetCacheFilePath(vkId);

    /// <summary>
    /// Cached track file: already downloaded — returns the path immediately; otherwise
    /// downloads the stream via the temporary URL and returns its path. Throws on HTTP
    /// failure (the .part file is removed).
    /// </summary>
    public Task<string> GetStreamFileAsync(string streamUrl, string vkId, CancellationToken ct)
        => _inner.GetStreamFileAsync(streamUrl, vkId, ct);
}
