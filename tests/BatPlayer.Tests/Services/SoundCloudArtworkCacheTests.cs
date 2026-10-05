using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

public class SoundCloudArtworkCacheTests : IDisposable
{
    private readonly string _dir;

    public SoundCloudArtworkCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "obsidian_artwork_cache_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SoundCloudArtworkCache CreateCache() => new(_dir);

    [Theory]
    [InlineData("123")]
    [InlineData("a-b_C")]
    public void GetCacheFilePath_NumericId_LandsInCacheDir(string scId)
    {
        var path = CreateCache().GetCacheFilePath(scId);

        Assert.Equal(Path.Combine(_dir, scId + ".jpg"), path);
    }

    [Fact]
    public void GetCacheFilePath_PathTraversalId_IsSanitized()
    {
        // id из БД не должен вывести путь наружу каталога кэша: всё, кроме [A-Za-z0-9_-], → '_'.
        var path = CreateCache().GetCacheFilePath("../../evil");

        Assert.StartsWith(_dir, path);
        // "../.." = 6 символов → 6 подчёркиваний, затем "evil".
        Assert.Equal(new string('_', 6) + "evil.jpg", Path.GetFileName(path));
    }

    [Fact]
    public void GetCacheFilePath_EmptyId_FallsBackToUnknown()
    {
        var path = CreateCache().GetCacheFilePath("");

        Assert.Equal(Path.Combine(_dir, "unknown.jpg"), path);
    }

    [Fact]
    public void IsCachedFile_MissingOrEmptyFile_IsFalse()
    {
        var cache = CreateCache();
        var path = cache.GetCacheFilePath("42");

        Assert.False(cache.IsCachedFile(path)); // файла нет

        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, string.Empty);
        Assert.False(cache.IsCachedFile(path)); // нулевой файл — след оборванной записи
    }

    [Fact]
    public async Task EnsureDownloadedAsync_AlreadyCached_ReturnsPathWithoutNetwork()
    {
        var cache = CreateCache();
        Directory.CreateDirectory(_dir);
        var path = cache.GetCacheFilePath("42");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        // URL здесь любой: файл уже в кэше, до сети дело не доходит.
        var result = await cache.EnsureDownloadedAsync("42", "https://i1.sndcdn.com/artworks-42-t500x500.jpg", CancellationToken.None);

        Assert.Equal(path, result);
    }

    [Fact]
    public async Task EnsureDownloadedAsync_EmptyUrl_ReturnsNull()
    {
        var cache = CreateCache();

        Assert.Null(await cache.EnsureDownloadedAsync("42", "", CancellationToken.None));
        Assert.Null(await cache.EnsureDownloadedAsync("42", null, CancellationToken.None));
    }
}
