using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты дискового кэша mp3-стримов SoundCloud: формат имени кэш-файла,
/// поведение «файл уже скачан» (без сети) и чистка каталога по лимиту.
/// Каталог кэша инжектится во временную папку — сеть не используется.
/// </summary>
public class SoundCloudStreamCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc_cache_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка — не критично */ }
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

        // '../evil' не должен выйти за пределы каталога кэша: '.','.','/' → три '_'.
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

        // URL на TLD .invalid: если код попытался в сеть — запрос упал бы и тест провалился.
        var result = await cache.GetStreamFileAsync("https://cache.invalid/stream/424242", "424242",
            CancellationToken.None);

        Assert.Equal(cached, result);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(result));
    }

    [Fact]
    public void EnforceLimit_DeletesOldestFilesUntilTarget()
    {
        // 3 файла по 60 байт = 180 > max 100; чистим до target 60: останется только новейший.
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

        // Учитываются только .mp3: 60 <= max, ничего не удаляется; .part и .txt не тронуты.
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
