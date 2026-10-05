using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Database;
using BatPlayer.Models;
using BatPlayer.Services;
using Xunit;

namespace BatPlayer.Tests.Services;

public class PlaylistServiceTests
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


    private static async Task<(string path, LibraryService lib, PlaylistService pls)> CreateAsync()
    {
        var tmp = Path.GetTempFileName();
        await DatabaseContext.InitializeAsync(tmp);
        var conn = new SqliteConnection($"Data Source={tmp};Cache=Shared");
        await conn.OpenAsync();
        var lib = new LibraryService(conn, new MetadataService(), _coverDir);
        var pls = new PlaylistService(conn);
        return (tmp, lib, pls);
    }

    [Fact]
    public async Task CreatePlaylist_ReturnsValidId()
    {
        var (path, lib, pls) = await CreateAsync();
        try
        {
            var id = await pls.CreatePlaylistAsync("MyList");
            Assert.True(id > 0);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task AddAndRetrieveTrackInPlaylist_Works()
    {
        var (path, lib, pls) = await CreateAsync();
        try
        {
            var pid = await lib.CreatePlaylistAsync("MyList");
            var tid = await lib.InsertOrUpdateTrackAsync(new Track
            {
                FilePath = "C:\\music\\x.mp3",
                Title = "X",
                Format = "MP3",
                DateAdded = System.DateTime.UtcNow
            });

            await lib.AddTrackToPlaylistAsync(pid, tid);
            var tracks = await lib.GetPlaylistTracksAsync(pid);
            Assert.Single(tracks);
            Assert.Equal("X", tracks[0].Title);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task RenamePlaylist_UpdatesName()
    {
        var (path, lib, pls) = await CreateAsync();
        try
        {
            var pid = await lib.CreatePlaylistAsync("Old");
            await lib.RenamePlaylistAsync(pid, "New");

            var lists = await lib.GetAllPlaylistsAsync();
            Assert.Equal("New", lists[0].Name);
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task DeletePlaylist_RemovesIt()
    {
        var (path, lib, pls) = await CreateAsync();
        try
        {
            var pid = await lib.CreatePlaylistAsync("ToDelete");
            await lib.DeletePlaylistAsync(pid);
            Assert.Empty(await lib.GetAllPlaylistsAsync());
        }
        finally { DeleteDb(path); }
    }
}
