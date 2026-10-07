using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// Shared disk cache of cover images: downloads artwork_url into a local file through the
/// owning service's HTTP transport and returns the path. Cards bind the local file (they
/// would otherwise bypass the user's proxy), and covers keep working offline. Files are
/// downloaded to a .part name and published atomically, so a truncated download never
/// lands in the cache as valid. A per-file failure (HTTP != 200, IOException) logs and
/// returns null; the caller's batch keeps going.
/// </summary>
public abstract class ArtworkCache
{
    private const string Extension = ".jpg";

    private readonly string _cacheDir;
    private readonly string _logName;

    protected ArtworkCache(string? cacheDir, string logName)
    {
        _cacheDir = cacheDir ?? Path.Combine(App.AppDataDir, "artworks_cache");
        _logName = logName;
    }

    /// <summary>Transport of the owning service (direct <-> proxy failover chain).</summary>
    protected abstract Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct);

    /// <summary>Cache directory (for diagnostics/tests).</summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Cache file path for an id. Everything outside [A-Za-z0-9_-] becomes '_' so an id
    /// coming from the database cannot escape the cache directory.
    /// </summary>
    public string GetCacheFilePath(string id)
    {
        var safe = new string((id ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
            .ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_cacheDir, safe + Extension);
    }

    /// <summary>File exists and is non-empty (an empty file is a truncated-write trace).</summary>
    public bool IsCachedFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    /// <summary>Cached path when already downloaded, otherwise downloads; null on failure.</summary>
    public async Task<string?> EnsureDownloadedAsync(string id, string? artworkUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(artworkUrl)) return null;

        var finalPath = GetCacheFilePath(id);
        if (IsCachedFile(finalPath)) return finalPath;

        Directory.CreateDirectory(_cacheDir);
        var tempPath = finalPath + ".part";
        try
        {
            var (status, stream, owner) = await SendStreamAsync(
                () => new HttpRequestMessage(HttpMethod.Get, artworkUrl), ct);

            using (owner)
            {
                if (status != 200 || stream == null)
                {
                    Logger.Warn($"{_logName} artwork download failed with HTTP {status} ({id})");
                    return null;
                }

                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await stream.CopyToAsync(file, ct);
            }

            // Atomic publish: only the .part file is visible before the move.
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{_logName} artwork download failed ({id})");
            TryDelete(tempPath);
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{_logName} artwork temp cleanup failed ({Path.GetFileName(path)})");
        }
    }
}
