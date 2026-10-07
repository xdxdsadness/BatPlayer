using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Database;
using Xunit;

namespace BatPlayer.Tests.Database;

public class DatabaseContextTests
{
    [Fact]
    public async Task InitializeAsync_CreatesAllExpectedTables()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);

            await using var conn = new SqliteConnection($"Data Source={tmp}");
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";

            var tables = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));

            Assert.Contains("tracks", tables);
            Assert.Contains("playlists", tables);
            Assert.Contains("playlist_tracks", tables);
            Assert.Contains("history", tables);
            Assert.Contains("playback_state", tables);
            Assert.Contains("library_folders", tables);
            Assert.Contains("artists", tables);
            Assert.Contains("albums", tables);
            Assert.Contains("genres", tables);
            Assert.Contains("schema_version", tables);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_RunTwiceDoesNotThrow()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);
            await DatabaseContext.InitializeAsync(tmp); // should not throw
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_FreshDb_HasArtworkLocalPathColumn()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await DatabaseContext.InitializeAsync(tmp);

            var columns = await GetSoundcloudLikesColumnsAsync(tmp);
            Assert.Contains("artwork_local_path", columns);
        }
        finally
        {
            Cleanup(tmp);
        }
    }

    [Fact]
    public async Task InitializeAsync_V1DbWithoutColumn_MigratesArtworkLocalPath()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // Simulate a v1 DB: likes table without artwork_local_path + schema_version = 1.
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp}"))
            {
                await conn.OpenAsync();
                var sql = """
                    CREATE TABLE soundcloud_likes (
                        sc_id         TEXT PRIMARY KEY,
                        title         TEXT NOT NULL DEFAULT '',
                        artist        TEXT NOT NULL DEFAULT '',
                        duration_ms   INTEGER NOT NULL DEFAULT 0,
                        artwork_url   TEXT NOT NULL DEFAULT '',
                        permalink_url TEXT NOT NULL DEFAULT '',
                        streamable    INTEGER NOT NULL DEFAULT 0,
                        liked_at      TEXT,
                        synced_at     TEXT NOT NULL DEFAULT ''
                    );
                    INSERT INTO soundcloud_likes(sc_id, title, artist) VALUES('42', 'Song', 'artist');
                    CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
                    INSERT INTO schema_version(version) VALUES(1);
                    """;
                await using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await DatabaseContext.InitializeAsync(tmp);

            var columns = await GetSoundcloudLikesColumnsAsync(tmp);
            Assert.Contains("artwork_local_path", columns);
            Assert.Contains("sc_id", columns); // migration keeps existing columns

            // Data survived the migration; the new column is empty.
            await using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp}");
            await check.OpenAsync();
            var select = check.CreateCommand();
            select.CommandText = "SELECT sc_id, artwork_local_path FROM soundcloud_likes";
            await using var r = await select.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("42", r.GetString(0));
            Assert.True(await r.IsDBNullAsync(1));
        }
        finally
        {
            Cleanup(tmp);
        }
    }

    private static async Task<List<string>> GetSoundcloudLikesColumnsAsync(string dbPath)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(soundcloud_likes)";
        var columns = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) columns.Add(r.GetString(1));
        return columns;
    }

    private static void Cleanup(string tmp)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }
}
