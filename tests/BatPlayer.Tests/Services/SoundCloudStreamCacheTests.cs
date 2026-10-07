using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Tests for the SoundCloud mp3 stream disk cache: cache file name format,
/// "already cached" behavior (no network) and directory cleanup by size limit.
/// The cache directory is injected into a temp folder — no network used.
/// </summary>
public class SoundCloudStreamCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc_cache_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir — ignore */ }
    }

    [Fact]
    public void GetCacheFilePath_FormatsScIdAsMp3Name()
    {
        var cache = new SoundCloudStreamCache(_dir);

        Assert.Equal(Path.Combine(_dir, "912345678.mp3"), cache.GetCacheFilePath("912345678"));
    }

    [Fact]
    public void GetCacheFilePath_SanitizesUnsafeCharacters()
    {
        var cache = new SoundCloudStreamCache(_dir);

        // '../evil' must not escape the cache dir: '.','.','/' → three '_'.
        var path = cache.GetCacheFilePath("../evil");

        Assert.Equal(Path.Combine(_dir, "___evil.mp3"), path);
        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(path));
    }

    [Fact]
    public void GetCacheFilePath_EmptyIdUsesFallbackName()
    {
        var cache = new SoundCloudStreamCache(_dir);

        Assert.Equal(Path.Combine(_dir, "unknown.mp3"), cache.GetCacheFilePath(""));
    }

    [Fact]
    public async Task GetStreamFileAsync_FileAlreadyCached_ReturnsWithoutNetwork()
    {
        var cache = new SoundCloudStreamCache(_dir);
        Directory.CreateDirectory(_dir);
        var cached = Path.Combine(_dir, "424242.mp3");
        await File.WriteAllBytesAsync(cached, new byte[] { 1, 2, 3, 4 });

        // .invalid TLD url: any network attempt would fail the request and the test.
        var result = await cache.GetStreamFileAsync("https://cache.invalid/stream/424242", "424242",
            CancellationToken.None);

        Assert.Equal(cached, result);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(result));
    }

    [Fact]
    public void EnforceLimit_DeletesOldestFilesUntilTarget()
    {
        // 3 files of 60 bytes = 180 > max 100; trim to target 60: only the newest survives.
        var cache = new SoundCloudStreamCache(_dir, maxCacheBytes: 100, targetCacheBytes: 60);
        WriteCachedFile("a_old.mp3", 60, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteCachedFile("b_mid.mp3", 60, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteCachedFile("c_new.mp3", 60, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        cache.EnforceLimit();

        var remaining = Directory.GetFiles(_dir, "*.mp3").Select(Path.GetFileName).ToList();
        var expected = Path.GetFileName(cache.GetCacheFilePath("c_new"));
        Assert.Equal(new[] { expected }, remaining);
    }

    [Fact]
    public void EnforceLimit_IgnoresPartAndNonMp3Files()
    {
        var cache = new SoundCloudStreamCache(_dir, maxCacheBytes: 100, targetCacheBytes: 60);
        WriteCachedFile("busy.mp3", 60, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteCachedFile("busy.mp3.part", 60, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteCachedFile("notes.txt", 60, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        cache.EnforceLimit();

        // Only .mp3 counted: 60 <= max, nothing deleted; .part and .txt untouched.
        Assert.Equal(new[] { "busy.mp3", "busy.mp3.part", "notes.txt" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList());
    }

    private void WriteCachedFile(string name, int size, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }
}
