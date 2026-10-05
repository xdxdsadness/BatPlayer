using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Database;
using BatPlayer.Models;
using BatPlayer.Services;
using Xunit;

namespace BatPlayer.Tests.Services;

public class LibraryServiceTests
{

    private static readonly string _coverDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "obsitest_covers");

    // SQLite connections opened in CreateAsync hold the temp db file; dispose
    // them before deleting, otherwise File.Delete throws IOException.
    private static readonly System.Collections.Generic.List<Microsoft.Data.Sqlite.SqliteConnection> _conns = new();

    private static void DeleteDb(params string[] paths)
    {
        foreach (var c in _conns) { try { c.Dispose(); } catch { } }
        _conns.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var p in paths)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }


    private static async Task<(string path, LibraryService svc)> CreateAsync()
    {
        var tmp = Path.GetTempFileName();
        await DatabaseContext.InitializeAsync(tmp);
        var conn = new SqliteConnection($"Data Source={tmp};Cache=Shared");
        await conn.OpenAsync();
        _conns.Add(conn);
        return (tmp, new LibraryService(conn, new MetadataService(), _coverDir));
    }

    private static Track MakeTrack(string path, string title = "T") => new()
    {
        FilePath = path,
        Title = title,
        Artist = "Tester",
        Album = "Test Album",
        Format = "MP3",
        DateAdded = DateTime.UtcNow
    };

    [Fact]
    public async Task InsertTrack_PersistsAndCanBeQueried()
    {
        var (path, svc) = await CreateAsync();
        try
        {
            var id = await svc.InsertOrUpdateTrackAsync(MakeTrack("C:\\music\\test.mp3", "Test"));
            Assert.True(id > 0);

            var loaded = await svc.GetTrackByIdAsync(id);
            Assert.NotNull(loaded);
            Assert.Equal("Test", loaded!.Title);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task InsertTrack_TwiceOnSamePath_DoesNotDuplicate()
    {
        var (path, svc) = await CreateAsync();
        try
        {
            var id1 = await svc.InsertOrUpdateTrackAsync(MakeTrack("C:\\dup.mp3", "V1"));
            var id2 = await svc.InsertOrUpdateTrackAsync(MakeTrack("C:\\dup.mp3", "V2"));

            Assert.Equal(id1, id2);
            var loaded = await svc.GetTrackByIdAsync(id1);
            Assert.Equal("V2", loaded!.Title);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task SetFavorite_UpdatesFlag()
    {
        var (path, svc) = await CreateAsync();
        try
        {
            var id = await svc.InsertOrUpdateTrackAsync(MakeTrack("C:\\fav.mp3"));
            await svc.SetFavoriteAsync(id, true);

            var fav = await svc.GetFavoritesAsync();
            Assert.Single(fav);

            await svc.SetFavoriteAsync(id, false);
            Assert.Empty(await svc.GetFavoritesAsync());
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task RemoveTrackFromLibrary_DeletesRow()
    {
        var (path, svc) = await CreateAsync();
        try
        {
            var id = await svc.InsertOrUpdateTrackAsync(MakeTrack("C:\\del.mp3"));
            await svc.RemoveTrackFromLibraryAsync(id);
            Assert.Null(await svc.GetTrackByIdAsync(id));
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task GetAllTracks_RespectsSortDirection()
    {
        var (path, svc) = await CreateAsync();
        try
        {
            await svc.InsertOrUpdateTrackAsync(MakeTrack("a.mp3", "Alpha"));
            await svc.InsertOrUpdateTrackAsync(MakeTrack("b.mp3", "Beta"));
            await svc.InsertOrUpdateTrackAsync(MakeTrack("c.mp3", "Gamma"));

            var asc = await svc.GetAllTracksAsync(SortColumn.Title, SortDirection.Ascending);
            Assert.Equal("Alpha", asc[0].Title);

            var desc = await svc.GetAllTracksAsync(SortColumn.Title, SortDirection.Descending);
            Assert.Equal("Gamma", desc[0].Title);
        }
        finally { DeleteDb(path); }
    }
}
