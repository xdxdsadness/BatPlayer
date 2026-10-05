using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// Кэш обложек альбомов на диске. Имя файла = SHA256(coverBytes).jpg.
/// При запросе обложки сервис сначала проверяет кэш, иначе извлекает из файла.
/// </summary>
public sealed class CoverCacheService
{
    private readonly string _cacheDir;
    private readonly MetadataService _meta;

    public CoverCacheService(string cacheDir, MetadataService? meta = null)
    {
        _cacheDir = cacheDir;
        _meta = meta ?? new MetadataService();
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string?> GetOrCreateCoverAsync(string filePath, string coverHash)
    {
        if (string.IsNullOrEmpty(coverHash)) return null;

        var cached = Path.Combine(_cacheDir, coverHash + ".jpg");
        if (File.Exists(cached)) return cached;

        var bytes = await Task.Run(() => _meta.ExtractCoverBytes(filePath));
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            await File.WriteAllBytesAsync(cached, bytes);
            return cached;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cover cache write failed");
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_cacheDir))
                File.Delete(f);
        }
        catch (Exception ex) { Logger.Error(ex, "Cover cache clear failed"); }
    }

    public long GetCacheSizeBytes()
    {
        try
        {
            return Directory.EnumerateFiles(_cacheDir).Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }
}
