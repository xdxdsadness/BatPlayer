using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Database;
using Xunit;

namespace BatPlayer.Tests.Database;

public class SoundCloudLikesRepositoryTests
{
    private static SoundCloudLikeRow Row(string scId, string title, string artist,
        long durationMs = 100_000, bool streamable = true, string? likedAt = null)
        => new()
        {
            ScId = scId,
            Title = title,
            Artist = artist,
            DurationMs = durationMs,
            ArtworkUrl = $"https://i1.sndcdn.com/artworks-{scId}-t500x500.jpg",
            PermalinkUrl = $"https://soundcloud.com/{artist}/{title}",
            Streamable = streamable,
            LikedAt = likedAt ?? "2026-09-01T00:00:00Z",
            SyncedAt = "2026-09-15T00:00:00Z"
        };

    private static async Task<(string path, SoundCloudLikesRepository repo)> CreateRepoAsync()
    {
        var tmp = Path.GetTempFileName();
        await DatabaseContext.InitializeAsync(tmp);
        var conn = new SqliteConnection($"Data Source={tmp}");
        await conn.OpenAsync();
        return (tmp, new SoundCloudLikesRepository(conn));
    }

    private static async Task CleanupAsync(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_CreatesSoundcloudLikesTable()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);

            await using var conn = new SqliteConnection($"Data Source={tmp}");
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='soundcloud_likes'";
            var name = (string?)await cmd.ExecuteScalarAsync();

            Assert.Equal("soundcloud_likes", name);
        }
        finally
        {
            await CleanupAsync(tmp);
        }
    }

    [Fact]
    public async Task UpsertBatchAsync_InsertsNewRows()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow>
            {
                Row("1", "Song One", "artist-a"),
                Row("2", "Song Two", "artist-b", streamable: false)
            });

            Assert.Equal(2, await repo.CountAsync());
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task UpsertBatchAsync_RerunUpdatesWithoutDuplicates()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { Row("1", "Song One", "artist-a") });

            var changed = Row("1", "Song One (Renamed)", "artist-a");
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { changed });

            Assert.Equal(1, await repo.CountAsync());
            var all = await repo.GetAllAsync();
            Assert.Equal("Song One (Renamed)", all[0].Title);
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task GetAllAsync_OrdersByLikedAtDescending()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow>
            {
                Row("old", "Old Like", "a", likedAt: "2026-01-01T00:00:00Z"),
                Row("new", "New Like", "b", likedAt: "2026-09-01T00:00:00Z"),
                Row("mid", "Mid Like", "c", likedAt: "2026-05-01T00:00:00Z"),
            });

            var all = await repo.GetAllAsync();

            Assert.Equal(new[] { "new", "mid", "old" }, new[] { all[0].ScId, all[1].ScId, all[2].ScId });
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task GetAllAsync_MapsAllColumns()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            var source = Row("77", "Deep Song", "artist-x",
                durationMs: 250_500, streamable: false, likedAt: "2026-03-03T03:03:03Z");
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { source });

            var rows = await repo.GetAllAsync();

            var row = Assert.Single(rows);
            Assert.Equal("77", row.ScId);
            Assert.Equal("Deep Song", row.Title);
            Assert.Equal("artist-x", row.Artist);
            Assert.Equal(250_500, row.DurationMs);
            Assert.Equal(source.ArtworkUrl, row.ArtworkUrl);
            Assert.Equal(source.PermalinkUrl, row.PermalinkUrl);
            Assert.False(row.Streamable);
            Assert.Equal("2026-03-03T03:03:03Z", row.LikedAt);
            Assert.Equal("2026-09-15T00:00:00Z", row.SyncedAt);
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task ClearAllAsync_RemovesEverything()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow>
            {
                Row("1", "A", "a"), Row("2", "B", "b")
            });
            Assert.Equal(2, await repo.CountAsync());

            await repo.ClearAllAsync();

            Assert.Equal(0, await repo.CountAsync());
            Assert.Empty(await repo.GetAllAsync());
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task SetArtworkLocalPathAsync_PersistsAndGetAllReturnsIt()
    {
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { Row("42", "Song", "artist") });

            await repo.SetArtworkLocalPathAsync("42", @"C:\cache\artworks_cache\42.jpg");

            var all = await repo.GetAllAsync();
            var row = Assert.Single(all);
            Assert.Equal(@"C:\cache\artworks_cache\42.jpg", row.ArtworkLocalPath);
        }
        finally
        {
            await CleanupAsync(path);
        }
    }

    [Fact]
    public async Task UpsertBatchAsync_DoesNotResetArtworkLocalPath()
    {
        // Пере-синк метаданных не должен затирать уже скачанные обложки.
        var (path, repo) = await CreateRepoAsync();
        try
        {
            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { Row("42", "Song", "artist") });
            await repo.SetArtworkLocalPathAsync("42", @"C:\cache\artworks_cache\42.jpg");

            await repo.UpsertBatchAsync(new List<SoundCloudLikeRow> { Row("42", "Song (Renamed)", "artist") });

            var all = await repo.GetAllAsync();
            var row = Assert.Single(all);
            Assert.Equal("Song (Renamed)", row.Title);
            Assert.Equal(@"C:\cache\artworks_cache\42.jpg", row.ArtworkLocalPath);
        }
        finally
        {
            await CleanupAsync(path);
        }
    }
}
