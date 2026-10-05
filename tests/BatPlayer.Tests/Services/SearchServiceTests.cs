using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Database;
using BatPlayer.Models;
using BatPlayer.Services;
using Xunit;

namespace BatPlayer.Tests.Services;

public class SearchServiceTests
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


    [Fact]
    public async Task Search_FindsByTitle_CaseInsensitive()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);
            var conn = new SqliteConnection($"Data Source={tmp};Cache=Shared");
            await conn.OpenAsync();
            var lib = new LibraryService(conn, new MetadataService(), _coverDir);
            var search = new SearchService(conn, _coverDir);

            await lib.InsertOrUpdateTrackAsync(new Track
            {
                FilePath = "C:\\1.mp3", Title = "Bohemian Rhapsody", Artist = "Queen",
                Format = "MP3", DateAdded = System.DateTime.UtcNow
            });
            await lib.InsertOrUpdateTrackAsync(new Track
            {
                FilePath = "C:\\2.mp3", Title = "Another One Bites the Dust", Artist = "Queen",
                Format = "MP3", DateAdded = System.DateTime.UtcNow
            });

            var byTitle = await search.SearchTracksAsync("rhapsody");
            Assert.Single(byTitle);

            var byArtist = await search.SearchTracksAsync("queen");
            Assert.Equal(2, byArtist.Count);
        }
        finally { DeleteDb(tmp); }
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyList()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);
            var conn = new SqliteConnection($"Data Source={tmp};Cache=Shared");
            await conn.OpenAsync();
            var search = new SearchService(conn, _coverDir);

            var results = await search.SearchTracksAsync("");
            Assert.Empty(results);
        }
        finally { DeleteDb(tmp); }
    }

    [Fact]
    public async Task Search_HandlesSpecialCharacters_Gracefully()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);
            var conn = new SqliteConnection($"Data Source={tmp};Cache=Shared");
            await conn.OpenAsync();
            var search = new SearchService(conn, _coverDir);

            // Should not throw on %, _, \
            var results = await search.SearchTracksAsync("50%_off\\deal");
            Assert.Empty(results);
        }
        finally { DeleteDb(tmp); }
    }
}
