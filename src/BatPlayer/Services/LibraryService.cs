using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using BatPlayer.Helpers;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>A play_log row for a platform track (SoundCloud/VK/Yandex/Spotify):
/// the track itself is not in tracks; metadata is a snapshot taken at listen time.</summary>
public sealed class LibraryServicePlatformPlay
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string Source { get; set; } = string.Empty;
    /// <summary>platform_id from play_log (ym_id/vk_id/sc_id): listens outside the
    /// catalog (wave recommendations) are restored into a card from the snapshot + id.</summary>
    public string PlatformId { get; set; } = string.Empty;
    public string? ArtworkPath { get; set; }
    public string PlayedAt { get; set; } = string.Empty;
}

/// <summary>A local track + listen moment (played_at from history): for a fair merge
/// of "Recently played" with platform play_log listens by common timestamp.</summary>
public sealed class LibraryServiceLocalPlay
{
    public Track Track { get; set; } = null!;
    public string PlayedAt { get; set; } = string.Empty;
}

/// <summary>
/// Library scanning + CRUD for tracks/playlists/history/playback_state.
/// All queries go through a single SqliteConnection with Cache=Shared;Dapper.ConcurrencyMode=AllowUnsafe.
/// </summary>
public sealed class LibraryService
{
    private readonly SqliteConnection _conn;
    private readonly MetadataService _meta;
    private readonly string _coverCacheDir;

    public LibraryService(SqliteConnection conn, MetadataService meta, string coverCacheDir)
    {
        _conn = conn;
        _meta = meta;
        _coverCacheDir = coverCacheDir;
    }

    /// <summary>cover_cache_path in the DB may be NULL — the path is rebuilt from the hash.</summary>
    private List<Track> AttachCovers(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
            if (t.CoverCachePath == null && !string.IsNullOrEmpty(t.CoverHash))
                t.CoverCachePath = Path.Combine(_coverCacheDir, t.CoverHash + ".jpg");
        return tracks as List<Track> ?? tracks.ToList();
    }

    public event EventHandler<(int done, int total, string current)>? ScanProgress;
    public event EventHandler? LibraryChanged;

    // ===== Tracks =====

    public async Task<Track?> GetTrackByIdAsync(long id)
        => AttachCovers((await _conn.QueryAsync<Track>("SELECT * FROM tracks WHERE id=@id", new { id })).ToList()).FirstOrDefault();

    public async Task<List<Track>> GetTracksByIdsAsync(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return new();
        var inClause = string.Join(",", list);
        var result = await _conn.QueryAsync<Track>($"SELECT * FROM tracks WHERE id IN ({inClause})");
        AttachCovers(result);
        // Preserve input order
        return list.Select(id => result.FirstOrDefault(t => t.Id == id)).Where(t => t != null).ToList()!;
    }

    public async Task<List<Track>> GetAllTracksAsync(SortColumn sort = SortColumn.DateAdded, SortDirection dir = SortDirection.Descending)
    {
        var col = sort switch
        {
            SortColumn.Title      => "title",
            SortColumn.Artist     => "artist",
            SortColumn.Album      => "album",
            SortColumn.Duration   => "duration_ticks",
            SortColumn.LastPlayed => "last_played",
            _                     => "date_added"
        };
        var ord = dir == SortDirection.Ascending ? "ASC" : "DESC";
        return AttachCovers((await _conn.QueryAsync<Track>($"SELECT * FROM tracks ORDER BY {col} {ord}")).ToList());
    }

    public async Task<List<Track>> GetRecentlyAddedAsync(int limit = 50)
        => AttachCovers((await _conn.QueryAsync<Track>(
            "SELECT * FROM tracks ORDER BY date_added DESC LIMIT @limit", new { limit })).ToList());

    public async Task<List<Track>> GetRecentlyPlayedAsync(int limit = 50)
        => AttachCovers((await _conn.QueryAsync<Track>(
            @"SELECT t.* FROM tracks t
              JOIN history h ON h.track_id = t.id
              WHERE h.last_position_ticks >= 0 OR h.play_count > 0
              ORDER BY h.played_at DESC LIMIT @limit", new { limit })).ToList());

    /// <summary>Same query as GetRecentlyPlayedAsync, but with played_at (ISO 8601
    /// UTC) included: "Recently played" mixes local tracks with platform play_log
    /// listens — they must be sorted by a common timestamp.</summary>
    public async Task<List<LibraryServiceLocalPlay>> GetRecentlyPlayedDatedAsync(int limit = 50)
    {
        var rows = (await _conn.QueryAsync<(long track_id, string played_at)>(
            @"SELECT track_id, played_at FROM history
              WHERE last_position_ticks >= 0 OR play_count > 0
              ORDER BY played_at DESC LIMIT @limit", new { limit })).ToList();
        if (rows.Count == 0) return new();

        var tracks = await GetTracksByIdsAsync(rows.Select(r => r.track_id));
        var byId = tracks.ToDictionary(t => t.Id);
        var result = new List<LibraryServiceLocalPlay>(rows.Count);
        foreach (var r in rows)
            if (byId.TryGetValue(r.track_id, out var t))
                result.Add(new LibraryServiceLocalPlay { Track = t, PlayedAt = r.played_at ?? string.Empty });
        return result;
    }

    /// <summary>Platform-track listens from play_log (source != 'local'): SoundCloud/
    /// VK/Yandex/Spotify cards have negative Ids and are absent from tracks, so a
    /// regular JOIN over history does not find them. Grouping by (source, title,
    /// artist) — the runtime Id is not persisted in the DB, matching is done on the
    /// metadata snapshot.</summary>
    public async Task<List<LibraryServicePlatformPlay>> GetRecentlyPlayedPlatformAsync(int limit = 100)
    {
        var sql = """
            SELECT track_title AS Title, track_artist AS Artist,
                   MAX(duration_ms) AS DurationMs, source AS Source,
                   MAX(platform_id) AS PlatformId,
                   MAX(artwork_path) AS ArtworkPath,
                   MAX(played_at) AS PlayedAt
            FROM play_log
            WHERE source != 'local'
            GROUP BY source, track_title, track_artist
            ORDER BY PlayedAt DESC
            LIMIT @limit
            """;
        return (await _conn.QueryAsync<LibraryServicePlatformPlay>(sql, new { limit })).ToList();
    }

    public async Task<List<Track>> GetFavoritesAsync()
        => AttachCovers((await _conn.QueryAsync<Track>("SELECT * FROM tracks WHERE is_favorite = 1 ORDER BY title")).ToList());

    public async Task SetFavoriteAsync(long trackId, bool fav)
    {
        await _conn.ExecuteAsync("UPDATE tracks SET is_favorite=@f WHERE id=@id", new { f = fav ? 1 : 0, id = trackId });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<long> InsertOrUpdateTrackAsync(Track t)
    {
        var sql = """
            INSERT INTO tracks (file_path, title, artist, album, genre, year, track_number,
                                duration_ticks, bitrate, sample_rate, channels, format,
                                file_size_bytes, cover_cache_path, cover_hash, date_added,
                                is_available)
            VALUES (@FilePath, @Title, @Artist, @Album, @Genre, @Year, @TrackNumber,
                    @DurationTicks, @Bitrate, @SampleRate, @Channels, @Format,
                    @FileSizeBytes, @CoverCachePath, @CoverHash, @DateAdded, @IsAvailable)
            ON CONFLICT(file_path) DO UPDATE SET
                title=@Title, artist=@Artist, album=@Album, genre=@Genre, year=@Year,
                track_number=@TrackNumber, duration_ticks=@DurationTicks, bitrate=@Bitrate,
                sample_rate=@SampleRate, channels=@Channels, format=@Format,
                file_size_bytes=@FileSizeBytes, cover_cache_path=@CoverCachePath,
                cover_hash=@CoverHash, is_available=1
            """;
        await _conn.ExecuteAsync(sql, t);
        var id = await _conn.ExecuteScalarAsync<long>("SELECT id FROM tracks WHERE file_path=@p", new { p = t.FilePath });
        t.Id = id;
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public async Task RemoveTrackFromLibraryAsync(long trackId)
    {
        await _conn.ExecuteAsync("DELETE FROM tracks WHERE id=@id", new { id = trackId });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteFileFromDiskAsync(long trackId)
    {
        var t = await GetTrackByIdAsync(trackId);
        if (t == null) return false;
        try
        {
            if (File.Exists(t.FilePath)) File.Delete(t.FilePath);
            await RemoveTrackFromLibraryAsync(trackId);
            return true;
        }
        catch (Exception ex) { Logger.Error(ex); return false; }
    }

    public async Task RefreshFileAvailabilityAsync()
    {
        var all = await _conn.QueryAsync<(long id, string file_path)>("SELECT id, file_path FROM tracks");
        foreach (var (id, path) in all)
        {
            var exists = File.Exists(path);
            await _conn.ExecuteAsync("UPDATE tracks SET is_available=@a WHERE id=@id",
                new { a = exists ? 1 : 0, id });
        }
    }

    // ===== Scan =====

    public async Task ScanFolderAsync(string folder, CancellationToken ct = default)
    {
        var files = Helpers.FileHelpers.EnumerateAudioFiles(folder).ToList();
        var total = files.Count;
        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) return;
            var path = files[i];
            ScanProgress?.Invoke(this, (i + 1, total, path));

            var track = await _meta.ReadAsync(path);
            if (track == null) continue;
            await InsertOrUpdateTrackAsync(track);
            await CacheCoverAsync(track, path);
        }
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Extracts the cover into the cache right during the scan so it is visible in the list.</summary>
    private async Task CacheCoverAsync(Track track, string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(track.CoverHash)) return;
            var target = Path.Combine(_coverCacheDir, track.CoverHash + ".jpg");
            if (File.Exists(target)) return;
            var bytes = await Task.Run(() => _meta.ExtractCoverBytes(filePath));
            if (bytes is { Length: > 0 })
                await File.WriteAllBytesAsync(target, bytes);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cover cache write failed");
        }
    }

    public async Task ScanAllConfiguredFoldersAsync(IEnumerable<string> folders, CancellationToken ct = default)
    {
        foreach (var f in folders)
        {
            if (ct.IsCancellationRequested) return;
            await ScanFolderAsync(f, ct);
        }
    }

    // ===== Playlists =====

    public async Task<List<Playlist>> GetAllPlaylistsAsync()
    {
        const string countSql = """
            SELECT p.*,
                   (SELECT COUNT(*) FROM playlist_tracks WHERE playlist_id = p.id)
                 + (SELECT COUNT(*) FROM playlist_platform_tracks WHERE playlist_id = p.id) AS TrackCount
            FROM playlists p
            ORDER BY updated_at DESC
            """;
        var playlists = (await _conn.QueryAsync<Playlist>(countSql)).ToList();
        return playlists;
    }

    public async Task<long> CreatePlaylistAsync(string name)
    {
        var p = new Playlist { Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var id = await _conn.ExecuteScalarAsync<long>(
            "INSERT INTO playlists(name, created_at, updated_at) VALUES(@n,@c,@u); SELECT last_insert_rowid();",
            new { n = p.Name, c = p.CreatedAt.ToString("o"), u = p.UpdatedAt.ToString("o") });
        p.Id = id;
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public async Task RenamePlaylistAsync(long id, string newName)
    {
        await _conn.ExecuteAsync("UPDATE playlists SET name=@n, updated_at=@u WHERE id=@id",
            new { n = newName, u = DateTime.UtcNow.ToString("o"), id });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeletePlaylistAsync(long id)
    {
        await _conn.ExecuteAsync("DELETE FROM playlists WHERE id=@id", new { id });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<List<Track>> GetPlaylistTracksAsync(long playlistId)
    {
        var sql = """
            SELECT t.*, pt.position
            FROM tracks t
            JOIN playlist_tracks pt ON pt.track_id = t.id
            WHERE pt.playlist_id = @pid
            ORDER BY pt.position
            """;
        var tracks = AttachCovers((await _conn.QueryAsync<Track>(sql, new { pid = playlistId })).ToList());

        // Platform tracks: runtime cards built from the snapshot (file resolved by FilePathResolver).
        var platform = (await _conn.QueryAsync<(string Source, string PlatformId, string Title, string Artist, long DurationMs)>(
            """
            SELECT source, platform_id, title, artist, duration_ms
            FROM playlist_platform_tracks
            WHERE playlist_id = @pid
            ORDER BY position
            """,
            new { pid = playlistId })).ToList();

        for (var i = 0; i < platform.Count; i++)
        {
            var r = platform[i];
            var t = YmRuntimeTracks.BuildRuntimeTrack(r.PlatformId, r.Title, r.Artist, r.DurationMs, null, true, i);
            t.Source = r.Source;
            // Platform track cover: SC/YM cache artwork in artworks_cache by platform
            // id — attach it, otherwise the playlist shows empty squares.
            t.CoverCachePath = ResolvePlatformArtwork(r.Source, r.PlatformId);
            tracks.Add(t);
        }
        return tracks;
    }

    /// <summary>Path to a platform track's cached cover (null — not downloaded).</summary>
    private string? ResolvePlatformArtwork(string source, string platformId)
    {
        var fileName = source switch
        {
            Models.Track.SourceSoundCloud => $"{platformId}.jpg",
            Models.Track.SourceYandex => $"ym_{platformId}.jpg",
            _ => null
        };
        if (fileName == null) return null;
        var path = Path.Combine(App.AppDataDir, "artworks_cache", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Adds a track to a playlist: local — by Id, platform — as a snapshot.</summary>
    public async Task AddTrackToPlaylistAsync(long playlistId, Track track)
    {
        if (track.Id > 0)
        {
            await AddTrackToPlaylistAsync(playlistId, track.Id);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var max = await _conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(position) FROM playlist_platform_tracks WHERE playlist_id=@p", new { p = playlistId });
        await _conn.ExecuteAsync("""
            INSERT INTO playlist_platform_tracks(playlist_id, source, platform_id, title, artist, duration_ms, position, added_at)
            VALUES(@p, @src, @pid2, @t, @a, @d, @pos, @now)
            """,
            new
            {
                p = playlistId,
                src = track.Source,
                pid2 = track.ScId,
                t = track.Title ?? string.Empty,
                a = track.Artist ?? string.Empty,
                d = Math.Max(0, track.DurationTicks / TimeSpan.TicksPerMillisecond),
                pos = (max ?? -1) + 1,
                now = DateTime.UtcNow.ToString("o")
            });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Playlist cover: a copy of the file into playlist_covers, path stored in the DB.</summary>
    public async Task SetPlaylistCoverAsync(long playlistId, string imagePath)
    {
        var dir = Path.Combine(App.AppDataDir, "playlist_covers");
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(imagePath);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        var target = Path.Combine(dir, playlistId + ext.ToLowerInvariant());
        File.Copy(imagePath, target, overwrite: true);
        await _conn.ExecuteAsync("UPDATE playlists SET cover_path=@c, updated_at=@u WHERE id=@id",
            new { c = target, u = DateTime.UtcNow.ToString("o"), id = playlistId });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddTrackToPlaylistAsync(long playlistId, long trackId, int? position = null)
    {
        if (position == null)
        {
            var max = await _conn.ExecuteScalarAsync<int?>(
                "SELECT MAX(position) FROM playlist_tracks WHERE playlist_id=@p", new { p = playlistId });
            position = (max ?? -1) + 1;
        }
        await _conn.ExecuteAsync(
            "INSERT INTO playlist_tracks(playlist_id, track_id, position) VALUES(@p,@t,@pos)",
            new { p = playlistId, t = trackId, pos = position });
        await _conn.ExecuteAsync("UPDATE playlists SET updated_at=@u WHERE id=@p",
            new { u = DateTime.UtcNow.ToString("o"), p = playlistId });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveTrackFromPlaylistAsync(long playlistId, long trackId, int position)
    {
        await _conn.ExecuteAsync(
            "DELETE FROM playlist_tracks WHERE playlist_id=@p AND track_id=@t AND position=@pos",
            new { p = playlistId, t = trackId, pos = position });
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReorderPlaylistTrackAsync(long playlistId, int fromPos, int toPos)
    {
        await using var tx = await _conn.BeginTransactionAsync();
        var tracks = (await _conn.QueryAsync<(long id, int position)>(
            "SELECT track_id, position FROM playlist_tracks WHERE playlist_id=@p ORDER BY position",
            new { p = playlistId }, tx)).ToList();
        if (fromPos < 0 || fromPos >= tracks.Count) return;
        var moved = tracks[fromPos];
        tracks.RemoveAt(fromPos);
        tracks.Insert(toPos, moved);
        for (int i = 0; i < tracks.Count; i++)
            await _conn.ExecuteAsync("UPDATE playlist_tracks SET position=@pos WHERE playlist_id=@p AND track_id=@t",
                new { pos = i, p = playlistId, t = tracks[i].id }, tx);
        await tx.CommitAsync();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== History =====

    public async Task<List<HistoryEntry>> GetHistoryAsync(int limit = 100)
    {
        var sql = """
            SELECT h.track_id AS TrackId, h.played_at AS PlayedAt, h.play_count AS PlayCount,
                   h.last_position_ticks AS LastPositionTicks
            FROM history h ORDER BY h.played_at DESC LIMIT @limit
            """;
        var entries = (await _conn.QueryAsync<HistoryEntry>(sql, new { limit })).ToList();
        if (entries.Count == 0) return entries;
        var tracks = await GetTracksByIdsAsync(entries.Select(e => e.TrackId));
        foreach (var e in entries) e.Track = tracks.FirstOrDefault(t => t.Id == e.TrackId);
        return entries;
    }

    public async Task<PlaybackState?> LoadPlaybackStateAsync()
    {
        var row = (await _conn.QueryAsync<dynamic>(
            "SELECT * FROM playback_state WHERE id=1")).FirstOrDefault();
        if (row == null) return null;

        var idsJson = (string)row.queue_track_ids;
        var ids = System.Text.Json.JsonSerializer.Deserialize<List<long>>(idsJson ?? "[]") ?? new();
        return new PlaybackState
        {
            CurrentTrackId    = row.current_track_id,
            LastPositionTicks = (long)row.last_position_ticks,
            Volume            = (int)row.volume,
            IsMuted           = (int)row.is_muted == 1,
            IsShuffle         = (int)row.is_shuffle == 1,
            RepeatMode        = (RepeatMode)(int)row.repeat_mode,
            QueueTrackIds     = ids,
            QueueIndex        = (int)row.queue_index,
            LastPlaylistId    = row.last_playlist_id
        };
    }

    public async Task SavePlaybackStateAsync(PlaybackState s)
    {
        var idsJson = System.Text.Json.JsonSerializer.Serialize(s.QueueTrackIds);
        var sql = """
            INSERT INTO playback_state(id, current_track_id, last_position_ticks, volume, is_muted,
                is_shuffle, repeat_mode, queue_track_ids, queue_index, last_playlist_id, updated_at)
            VALUES(1, @CurrentTrackId, @LastPositionTicks, @Volume, @IsMuted, @IsShuffle, @RepeatMode,
                @QueueJson, @QueueIndex, @LastPlaylistId, @updated)
            ON CONFLICT(id) DO UPDATE SET
                current_track_id=@CurrentTrackId, last_position_ticks=@LastPositionTicks,
                volume=@Volume, is_muted=@IsMuted, is_shuffle=@IsShuffle, repeat_mode=@RepeatMode,
                queue_track_ids=@QueueJson, queue_index=@QueueIndex, last_playlist_id=@LastPlaylistId,
                updated_at=@updated
            """;
        await _conn.ExecuteAsync(sql, new
        {
            s.CurrentTrackId,
            s.LastPositionTicks,
            s.Volume,
            IsMuted = s.IsMuted ? 1 : 0,
            IsShuffle = s.IsShuffle ? 1 : 0,
            s.RepeatMode,
            QueueJson = idsJson,
            s.QueueIndex,
            s.LastPlaylistId,
            updated = DateTime.UtcNow.ToString("o")
        });
    }

    // ===== Library folders =====

    public async Task<List<Folder>> GetLibraryFoldersAsync()
        => (await _conn.QueryAsync<Folder>("SELECT * FROM library_folders ORDER BY added_at")).ToList();

    public async Task AddLibraryFolderAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        await _conn.ExecuteAsync(
            "INSERT INTO library_folders(path, added_at, auto_scan) VALUES(@p,@a,1) ON CONFLICT(path) DO NOTHING",
            new { p = path, a = DateTime.UtcNow.ToString("o") });
    }

    public async Task RemoveLibraryFolderAsync(string path)
        => await _conn.ExecuteAsync("DELETE FROM library_folders WHERE path=@p", new { p = path });

    // ===== Aggregations =====

    public async Task<List<Artist>> GetArtistsAsync()
    {
        var sql = """
            SELECT '' AS Id, artist AS Name, COUNT(*) AS TrackCount,
                   COUNT(DISTINCT album) AS AlbumCount
            FROM tracks WHERE is_available=1
            GROUP BY artist ORDER BY artist
            """;
        return (await _conn.QueryAsync<Artist>(sql)).ToList();
    }

    public async Task<List<Album>> GetAlbumsAsync()
    {
        var sql = """
            SELECT '' AS Id, album AS Title, MIN(artist) AS Artist, MIN(year) AS Year, COUNT(*) AS TrackCount
            FROM tracks WHERE is_available=1
            GROUP BY album ORDER BY album
            """;
        return (await _conn.QueryAsync<Album>(sql)).ToList();
    }

    public async Task<List<Genre>> GetGenresAsync()
    {
        var sql = """
            SELECT '' AS Id, genre AS Name, COUNT(*) AS TrackCount
            FROM tracks WHERE is_available=1 AND genre != ''
            GROUP BY genre ORDER BY genre
            """;
        return (await _conn.QueryAsync<Genre>(sql)).ToList();
    }
}
