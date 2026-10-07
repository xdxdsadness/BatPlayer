using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BatPlayer.Services;

namespace BatPlayer.Database;

/// <summary>
/// SQLite database initialization: creates the file, schema, indexes, and runs migrations.
/// </summary>
public static class DatabaseContext
{
    public const int CurrentSchemaVersion = 7;

    // v2: soundcloud_likes.artwork_local_path — local cache of like artworks
    // (artworks_cache/{scId}.jpg): i1.sndcdn.com is not directly reachable; covers
    // are downloaded via the SoundCloudHttp proxy layer and stored as local files.

    public static async Task InitializeAsync(string dbPath)
    {
        // Schema uses snake_case columns (file_path, duration_ticks, ...);
        // without this Dapper leaves multi-word properties (FilePath, ...) empty.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // WAL: concurrent reads (stats page, offline slices) are not blocked by
        // play-log writes and vice versa. Persistent mode — set once per database file.
        try
        {
            await using var wal = conn.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteScalarAsync();
        }
        catch
        {
            // Non-critical: on failure the database stays in delete mode.
        }

        await ExecuteSchemaAsync(conn);
        await RunMigrationsAsync(conn);
    }

    private static async Task ExecuteSchemaAsync(SqliteConnection conn)
    {
        var sql = """
            -- ===== tracks =====
            CREATE TABLE IF NOT EXISTS tracks (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path       TEXT NOT NULL UNIQUE,
                title           TEXT NOT NULL,
                artist          TEXT NOT NULL DEFAULT 'Неизвестный исполнитель',
                album           TEXT NOT NULL DEFAULT 'Неизвестный альбом',
                genre           TEXT NOT NULL DEFAULT '',
                year            INTEGER NOT NULL DEFAULT 0,
                track_number    INTEGER NOT NULL DEFAULT 0,
                duration_ticks  INTEGER NOT NULL DEFAULT 0,
                bitrate         INTEGER NOT NULL DEFAULT 0,
                sample_rate     INTEGER NOT NULL DEFAULT 0,
                channels        INTEGER NOT NULL DEFAULT 0,
                format          TEXT NOT NULL DEFAULT '',
                file_size_bytes INTEGER NOT NULL DEFAULT 0,
                cover_cache_path TEXT,
                cover_hash      TEXT NOT NULL DEFAULT '',
                date_added      TEXT NOT NULL,
                last_played     TEXT,
                play_count      INTEGER NOT NULL DEFAULT 0,
                last_position_ticks INTEGER NOT NULL DEFAULT 0,
                is_favorite     INTEGER NOT NULL DEFAULT 0,
                is_available    INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_tracks_title    ON tracks(title);
            CREATE INDEX IF NOT EXISTS idx_tracks_artist   ON tracks(artist);
            CREATE INDEX IF NOT EXISTS idx_tracks_album    ON tracks(album);
            CREATE INDEX IF NOT EXISTS idx_tracks_genre    ON tracks(genre);
            CREATE INDEX IF NOT EXISTS idx_tracks_added    ON tracks(date_added);
            CREATE INDEX IF NOT EXISTS idx_tracks_favorite ON tracks(is_favorite);

            -- ===== artists / albums / genres =====
            CREATE TABLE IF NOT EXISTS artists (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS albums (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                title  TEXT NOT NULL,
                artist TEXT NOT NULL,
                year   INTEGER NOT NULL DEFAULT 0,
                UNIQUE(title, artist)
            );
            CREATE TABLE IF NOT EXISTS genres (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );

            -- ===== folders =====
            CREATE TABLE IF NOT EXISTS library_folders (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT NOT NULL UNIQUE,
                added_at  TEXT NOT NULL,
                auto_scan INTEGER NOT NULL DEFAULT 1
            );

            -- ===== playlists =====
            CREATE TABLE IF NOT EXISTS playlists (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playlist_tracks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                playlist_id INTEGER NOT NULL,
                track_id    INTEGER NOT NULL,
                position    INTEGER NOT NULL,
                FOREIGN KEY(playlist_id) REFERENCES playlists(id) ON DELETE CASCADE,
                FOREIGN KEY(track_id)    REFERENCES tracks(id)    ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_playlist_tracks_pid ON playlist_tracks(playlist_id);

            -- Platform tracks in playlists (SoundCloud/VK/Yandex): metadata snapshot;
            -- no tracks.id — file resolved at playback via FilePathResolver.
            CREATE TABLE IF NOT EXISTS playlist_platform_tracks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                playlist_id INTEGER NOT NULL,
                source      TEXT NOT NULL,
                platform_id TEXT NOT NULL,
                title       TEXT NOT NULL DEFAULT '',
                artist      TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                position    INTEGER NOT NULL DEFAULT 0,
                added_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playlist_platform_pid ON playlist_platform_tracks(playlist_id);

            -- ===== history =====
            CREATE TABLE IF NOT EXISTS history (
                track_id    INTEGER PRIMARY KEY,
                played_at   TEXT,
                play_count  INTEGER NOT NULL DEFAULT 0,
                last_position_ticks INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(track_id) REFERENCES tracks(id) ON DELETE CASCADE
            );

            -- ===== play log: one row per play (for statistics) =====
            CREATE TABLE IF NOT EXISTS play_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                track_title   TEXT NOT NULL DEFAULT '',
                track_artist  TEXT NOT NULL DEFAULT '',
                duration_ms   INTEGER NOT NULL DEFAULT 0,
                source        TEXT NOT NULL DEFAULT 'local',
                platform_id   TEXT NOT NULL DEFAULT '',
                artwork_path  TEXT,
                played_at     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_play_log_time ON play_log(played_at);

            -- ===== settings/state =====
            CREATE TABLE IF NOT EXISTS playback_state (
                id                   INTEGER PRIMARY KEY CHECK(id = 1),
                current_track_id     INTEGER,
                last_position_ticks  INTEGER NOT NULL DEFAULT 0,
                volume               INTEGER NOT NULL DEFAULT 70,
                is_muted             INTEGER NOT NULL DEFAULT 0,
                is_shuffle           INTEGER NOT NULL DEFAULT 0,
                repeat_mode          INTEGER NOT NULL DEFAULT 0,
                queue_track_ids      TEXT NOT NULL DEFAULT '[]',
                queue_index          INTEGER NOT NULL DEFAULT 0,
                last_playlist_id     INTEGER,
                updated_at           TEXT NOT NULL
            );

            -- ===== soundcloud likes (metadata only, no files) =====
            CREATE TABLE IF NOT EXISTS soundcloud_likes (
                sc_id         TEXT PRIMARY KEY,
                title         TEXT NOT NULL DEFAULT '',
                artist        TEXT NOT NULL DEFAULT '',
                duration_ms   INTEGER NOT NULL DEFAULT 0,
                artwork_url   TEXT NOT NULL DEFAULT '',
                artwork_local_path TEXT,
                permalink_url TEXT NOT NULL DEFAULT '',
                streamable    INTEGER NOT NULL DEFAULT 0,
                liked_at      TEXT,
                synced_at     TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_soundcloud_likes_liked_at ON soundcloud_likes(liked_at);

            -- ===== vk music (metadata only, no files) =====
            CREATE TABLE IF NOT EXISTS vk_tracks (
                vk_id         TEXT PRIMARY KEY,
                title         TEXT NOT NULL DEFAULT '',
                artist        TEXT NOT NULL DEFAULT '',
                duration_ms   INTEGER NOT NULL DEFAULT 0,
                artwork_url   TEXT NOT NULL DEFAULT '',
                url_hash      TEXT NOT NULL DEFAULT '',
                artwork_local_path TEXT,
                synced_at     TEXT NOT NULL DEFAULT ''
            );

            -- ===== yandex music (metadata only, no files) =====
            CREATE TABLE IF NOT EXISTS ym_tracks (
                ym_id         TEXT PRIMARY KEY,
                title         TEXT NOT NULL DEFAULT '',
                artist        TEXT NOT NULL DEFAULT '',
                duration_ms   INTEGER NOT NULL DEFAULT 0,
                artwork_url   TEXT NOT NULL DEFAULT '',
                artwork_local_path TEXT,
                available     INTEGER NOT NULL DEFAULT 1,
                liked_at      TEXT,
                synced_at     TEXT NOT NULL DEFAULT ''
            );

            -- ===== My Wave: local recommendations =====

            -- Cache of /tracks/{id}/similar responses: seed ym_id -> similar tracks
            -- (candidate metadata snapshot). The whole response is stored row-by-row so
            -- wave generation does not hit the network per seed. TTL is enforced
            -- by RecommendationService via fetched_at.
            CREATE TABLE IF NOT EXISTS wave_similar (
                seed_ym_id  TEXT NOT NULL,
                ym_id       TEXT NOT NULL,
                rank        INTEGER NOT NULL DEFAULT 0,
                title       TEXT NOT NULL DEFAULT '',
                artist      TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                cover_uri   TEXT NOT NULL DEFAULT '',
                available   INTEGER NOT NULL DEFAULT 1,
                fetched_at  TEXT NOT NULL,
                PRIMARY KEY(seed_ym_id, ym_id)
            );
            CREATE INDEX IF NOT EXISTS idx_wave_similar_fetched ON wave_similar(seed_ym_id, fetched_at);

            -- Maps non-Yandex seeds (vk_tracks/soundcloud_likes/local) to ym_id via
            -- /search. ym_id='' means no match found (negative cache, so the search
            -- does not hammer the API on every generation).
            CREATE TABLE IF NOT EXISTS wave_seed_map (
                source      TEXT NOT NULL,
                seed_id     TEXT NOT NULL,
                ym_id       TEXT NOT NULL DEFAULT '',
                resolved_at TEXT NOT NULL,
                PRIMARY KEY(source, seed_id)
            );

            -- Tracks already suggested: repeated generations do not repeat the same list.
            -- played = the track was played after being suggested (positive: it may be
            -- suggested again); recently suggested unplayed tracks are excluded for a while.
            CREATE TABLE IF NOT EXISTS wave_suggested (
                ym_id        TEXT PRIMARY KEY,
                artist_key   TEXT NOT NULL DEFAULT '',
                title_key    TEXT NOT NULL DEFAULT '',
                suggested_at TEXT NOT NULL,
                played       INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_wave_suggested_time ON wave_suggested(suggested_at);

            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY
            );
            """;

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RunMigrationsAsync(SqliteConnection conn)
    {
        await using var check = new SqliteCommand("SELECT version FROM schema_version", conn);
        var current = 0;
        await using (var r = await check.ExecuteReaderAsync())
            if (await r.ReadAsync()) current = r.GetInt32(0);

        if (current >= CurrentSchemaVersion) return;

        if (current < 2)
        {
            // artwork_local_path: cover path in the local cache (artworks_cache/{scId}.jpg).
            // Fresh databases get the column from the schema; the PRAGMA check makes the ALTER idempotent.
            if (!await ColumnExistsAsync(conn, "soundcloud_likes", "artwork_local_path"))
            {
                await using var alter = new SqliteCommand(
                    "ALTER TABLE soundcloud_likes ADD COLUMN artwork_local_path TEXT", conn);
                await alter.ExecuteNonQueryAsync();
            }
        }

        if (current < 3)
        {
            // Playlist cover (uploaded file path) + platform tracks table.
            if (!await ColumnExistsAsync(conn, "playlists", "cover_path"))
            {
                await using var alter = new SqliteCommand(
                    "ALTER TABLE playlists ADD COLUMN cover_path TEXT", conn);
                await alter.ExecuteNonQueryAsync();
            }

            await using var plt = new SqliteCommand(
            """
            CREATE TABLE IF NOT EXISTS playlist_platform_tracks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                playlist_id INTEGER NOT NULL,
                source      TEXT NOT NULL,
                platform_id TEXT NOT NULL,
                title       TEXT NOT NULL DEFAULT '',
                artist      TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                position    INTEGER NOT NULL DEFAULT 0,
                added_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playlist_platform_pid ON playlist_platform_tracks(playlist_id);
            """, conn);
            await plt.ExecuteNonQueryAsync();
        }

        if (current < 4)
        {
            // url_hash: stream URL hash (reload_audio builds the mp3 URL from it).
            if (!await ColumnExistsAsync(conn, "vk_tracks", "url_hash"))
            {
                await using var alter = new SqliteCommand(
                    "ALTER TABLE vk_tracks ADD COLUMN url_hash TEXT NOT NULL DEFAULT ''", conn);
                await alter.ExecuteNonQueryAsync();
            }
        }

        if (current < 5)
        {
            // My Wave: similarity cache, seed-to-YM mapping, and suggestion log.
            // CREATE IF NOT EXISTS is idempotent: fresh databases already have the tables.
            await using var wave = new SqliteCommand(
            """
            CREATE TABLE IF NOT EXISTS wave_similar (
                seed_ym_id  TEXT NOT NULL,
                ym_id       TEXT NOT NULL,
                rank        INTEGER NOT NULL DEFAULT 0,
                title       TEXT NOT NULL DEFAULT '',
                artist      TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                cover_uri   TEXT NOT NULL DEFAULT '',
                available   INTEGER NOT NULL DEFAULT 1,
                fetched_at  TEXT NOT NULL,
                PRIMARY KEY(seed_ym_id, ym_id)
            );
            CREATE INDEX IF NOT EXISTS idx_wave_similar_fetched ON wave_similar(seed_ym_id, fetched_at);
            CREATE TABLE IF NOT EXISTS wave_seed_map (
                source      TEXT NOT NULL,
                seed_id     TEXT NOT NULL,
                ym_id       TEXT NOT NULL DEFAULT '',
                resolved_at TEXT NOT NULL,
                PRIMARY KEY(source, seed_id)
            );
            CREATE TABLE IF NOT EXISTS wave_suggested (
                ym_id        TEXT PRIMARY KEY,
                artist_key   TEXT NOT NULL DEFAULT '',
                title_key    TEXT NOT NULL DEFAULT '',
                suggested_at TEXT NOT NULL,
                played       INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_wave_suggested_time ON wave_suggested(suggested_at);
            """, conn);
            await wave.ExecuteNonQueryAsync();
        }

        if (current < 6)
        {
            // platform_id/artwork_path in play_log: plays of platform tracks missing from
            // the catalogs (e.g. My Wave recommendations absent among likes) are restored
            // on Recently Played from the snapshot + id.
            if (!await ColumnExistsAsync(conn, "play_log", "platform_id"))
            {
                await using var pid = new SqliteCommand(
                    "ALTER TABLE play_log ADD COLUMN platform_id TEXT NOT NULL DEFAULT ''", conn);
                await pid.ExecuteNonQueryAsync();
            }
            if (!await ColumnExistsAsync(conn, "play_log", "artwork_path"))
            {
                await using var art = new SqliteCommand(
                    "ALTER TABLE play_log ADD COLUMN artwork_path TEXT", conn);
                await art.ExecuteNonQueryAsync();
            }
        }

        if (current < 7)
        {
            // liked_at in ym_tracks: like time from the API (likes entry timestamp). The
            // catalog is sorted by it (newest likes on top) — rowid order keeps new likes
            // at the end of a long list, so they never surface in visible positions.
            if (!await ColumnExistsAsync(conn, "ym_tracks", "liked_at"))
            {
                await using var liked = new SqliteCommand(
                    "ALTER TABLE ym_tracks ADD COLUMN liked_at TEXT", conn);
                await liked.ExecuteNonQueryAsync();
            }
        }

        // Future migrations go here, e.g.:
        // if (current < 8) { ... }

        await using var upsert = new SqliteCommand(
            "INSERT INTO schema_version(version) VALUES(@v) ON CONFLICT(version) DO NOTHING", conn);
        upsert.Parameters.AddWithValue("@v", CurrentSchemaVersion);
        await upsert.ExecuteNonQueryAsync();
    }

    /// <summary>Whether the column exists in the table (PRAGMA table_info) — for idempotent ALTERs.</summary>
    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column)
    {
        await using var cmd = new SqliteCommand($"PRAGMA table_info({table})", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
