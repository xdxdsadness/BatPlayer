using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatPlayer.Database;

namespace BatPlayer.Services.Spotify;

/// <summary>
/// Импорт библиотеки Spotify БЕЗ Web API (с февраля 2026 Development Mode в
/// Spotify Dashboard требует Premium — бесплатный путь лежит мимо API):
///
/// 1. Официальный экспорт аккаунта «Download your data» (spotify.com → Аккаунт →
///    Настройки конфиденциальности): ZIP с YourLibrary.json (лайкнутые треки)
///    и Playlist*.json (треки плейлистов).
/// 2. CSV сторонних экспортёров (Exportify, TuneMyMusic и т.п.).
///
/// Формат полей в экспорте Spotify менялся годами, поэтому парсер толерантный:
/// trackName/track, artistName/artist, albumName/album, trackUri/uri.
/// Результат кладётся в тот же spotify_tracks, что и синк Web API, — страница,
/// матчинг и воспроизведение работают без изменений.
/// </summary>
public static class SpotifyImportService
{
    private static readonly JsonDocumentOptions DocOpts = new();

    /// <summary>
    /// Разобрать файл экспорта (.zip / .json / .csv) в строки репозитория.
    /// Бросает InvalidDataException/FormatException с человеческим сообщением,
    /// если формат не опознан.
    /// </summary>
    public static List<SpotifyTrackRow> ParseFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var byId = new Dictionary<string, SpotifyTrackRow>(StringComparer.Ordinal);

        switch (ext)
        {
            case ".zip":
                using (var zip = ZipFile.OpenRead(path))
                {
                    var libraryCount = 0;
                    var playlistCount = 0;

                    foreach (var entry in zip.Entries)
                    {
                        var name = Path.GetFileName(entry.FullName).ToLowerInvariant();
                        if (name.Length == 0 || !name.EndsWith(".json")) continue;

                        if (IsLibraryFileName(name))
                        {
                            using var stream = entry.Open();
                            libraryCount += ParseLibraryJson(stream, byId);
                        }
                        else if (IsPlaylistFileName(name))
                        {
                            using var stream = entry.Open();
                            playlistCount += ParsePlaylistJson(stream, byId);
                        }
                    }

                    // Экспорт без YourLibrary.json: лайки неотличимы, берём треки плейлистов.
                    if (libraryCount == 0 && playlistCount == 0)
                        throw new InvalidDataException(
                            "ZIP does not contain YourLibrary.json or Playlist*.json — " +
                            "is this really the Spotify «Download your data» archive?");

                    Logger.Info($"Spotify import (zip): library={libraryCount}, playlists={playlistCount}, unique={byId.Count}");
                }
                break;

            case ".json":
                using (var stream = File.OpenRead(path))
                {
                    var count = ParseAnyJson(stream, byId);
                    if (count == 0)
                        throw new InvalidDataException("No Spotify tracks recognized in this JSON file.");
                    Logger.Info($"Spotify import (json): {count} tracks, unique={byId.Count}");
                }
                break;

            case ".csv":
                ParseCsv(File.ReadAllText(path), byId);
                if (byId.Count == 0)
                    throw new InvalidDataException("No track columns recognized in this CSV file.");
                Logger.Info($"Spotify import (csv): {byId.Count} tracks");
                break;

            default:
                throw new NotSupportedException($"Unsupported import file type: {ext}");
        }

        return byId.Values.ToList();
    }

    private static bool IsLibraryFileName(string lowerName) =>
        lowerName.Replace("_", "").Replace(" ", "") == "yourlibrary.json";

    private static bool IsPlaylistFileName(string lowerName) =>
        lowerName.StartsWith("playlist") && lowerName.EndsWith(".json");

    // ============================ JSON ==================================

    private static int ParseAnyJson(Stream stream, Dictionary<string, SpotifyTrackRow> byId)
    {
        using var doc = JsonDocument.Parse(stream, DocOpts);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tracks", out _)
            ? ParseLibraryJson(root, byId)
            : ParsePlaylistJson(root, byId);
    }

    private static int ParseLibraryJson(Stream stream, Dictionary<string, SpotifyTrackRow> byId)
    {
        using var doc = JsonDocument.Parse(stream, DocOpts);
        return ParseLibraryJson(doc.RootElement, byId);
    }

    /// <summary>YourLibrary.json: { "tracks": [ {...}, ... ] }.</summary>
    private static int ParseLibraryJson(JsonElement root, Dictionary<string, SpotifyTrackRow> byId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var item in tracks.EnumerateArray())
            count += TryAddTrack(item, byId) ? 1 : 0;
        return count;
    }

    private static int ParsePlaylistJson(Stream stream, Dictionary<string, SpotifyTrackRow> byId)
    {
        using var doc = JsonDocument.Parse(stream, DocOpts);
        return ParsePlaylistJson(doc.RootElement, byId);
    }

    /// <summary>
    /// Playlist*.json в двух вариантах: { "playlists": [ { "items": [...] } ] } и
    /// одиночный { "items"|"contents": [...] }. Элементы — либо { "track": {...} },
    /// либо плоские объекты с теми же полями.
    /// </summary>
    private static int ParsePlaylistJson(JsonElement root, Dictionary<string, SpotifyTrackRow> byId)
    {
        var count = 0;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("playlists", out var playlists)
            && playlists.ValueKind == JsonValueKind.Array)
        {
            foreach (var playlist in playlists.EnumerateArray())
                count += ParsePlaylistItems(playlist, byId);
            return count;
        }

        return ParsePlaylistItems(root, byId);
    }

    private static int ParsePlaylistItems(JsonElement playlist, Dictionary<string, SpotifyTrackRow> byId)
    {
        if (playlist.ValueKind != JsonValueKind.Object) return 0;

        JsonElement items;
        if (playlist.TryGetProperty("items", out var i1) && i1.ValueKind == JsonValueKind.Array) items = i1;
        else if (playlist.TryGetProperty("contents", out var i2) && i2.ValueKind == JsonValueKind.Array) items = i2;
        else return 0;

        var count = 0;
        foreach (var item in items.EnumerateArray())
        {
            // Элементы плейлиста: { "track": {...} | null, "episode": {...}|null, "localTrack": {...}|null }
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (HasNonNull(item, "episode") || HasNonNull(item, "localTrack"))
                    continue;

                if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
                {
                    count += TryAddTrack(track, byId) ? 1 : 0;
                    continue;
                }
            }

            count += TryAddTrack(item, byId) ? 1 : 0;
        }
        return count;
    }

    private static bool HasNonNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    // ============================ CSV ===================================

    /// <summary>
    /// Минимальный RFC4180-парсер: кавычки, запятые и переносы внутри кавычек.
    /// Колонки мапятся по нормализованным именам (Exportify/TuneMyMusic/Soundiiz).
    /// </summary>
    private static void ParseCsv(string content, Dictionary<string, SpotifyTrackRow> byId)
    {
        var rows = SplitCsvRows(content);
        if (rows.Count == 0) return;

        var header = rows[0];
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
            map[NormalizeKey(header[i])] = i;

        int Col(params string[] names)
        {
            foreach (var name in names)
                if (map.TryGetValue(name, out var idx)) return idx;
            return -1;
        }

        var idCol = Col("spotifyid", "trackid", "id", "trackuri", "uri");
        var titleCol = Col("trackname", "title", "songtitle", "song", "name");
        var artistCol = Col("artistnames", "artistname", "artists", "artist");
        var albumCol = Col("albumname", "albumtitle", "album");
        var durationCol = Col("durationms", "duration", "length");
        var artworkCol = Col("artworkurl", "artwork", "coverurl", "cover", "imageurl", "image", "albumimageurl");
        var addedCol = Col("addedat", "dateadded", "added");

        if (titleCol < 0 || artistCol < 0)
            throw new InvalidDataException("CSV must have track title and artist columns.");

        for (var r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.Count == 0 || (cells.Count == 1 && cells[0].Length == 0)) continue;

            string Get(int col) => col >= 0 && col < cells.Count ? cells[col].Trim() : string.Empty;

            var title = Get(titleCol);
            var artist = Get(artistCol);
            if (title.Length == 0) continue;

            var id = ExtractTrackId(Get(idCol)) ?? SynthId(artist, title, Get(albumCol));
            var row = new SpotifyTrackRow
            {
                SpotifyId = id,
                Title = title,
                Artist = artist,
                Album = Get(albumCol),
                DurationMs = ParseDurationMs(Get(durationCol)),
                ArtworkUrl = Get(artworkCol),
                IsPlayable = true,
                AddedAt = Get(addedCol) is { Length: > 0 } ? Get(addedCol) : DateTime.UtcNow.ToString("o")
            };
            TryAdd(row, byId);
        }
    }

    private static string NormalizeKey(string header) =>
        new string(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static List<List<string>> SplitCsvRows(string content)
    {
        var rows = new List<List<string>>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                cells.Add(cell.ToString());
                cell.Clear();
                rows.Add(cells);
                cells = new List<string>();
                if (rows.Count == 1 && rows[0].Count == 1 && rows[0][0].Length == 0)
                    rows.Clear(); // пустая первая строка
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        rows.Add(cells);
        return rows;
    }

    // ========================= Track rows ===============================

    private static bool TryAddTrack(JsonElement el, Dictionary<string, SpotifyTrackRow> byId)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;

        var uri = GetString(el, "trackUri") ?? GetString(el, "uri");
        if (uri != null && !uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            return false; // эпизоды, локальные файлы, подкасты

        var title = GetString(el, "trackName") ?? GetString(el, "track") ?? GetString(el, "name");
        var artist = GetString(el, "artistName") ?? GetString(el, "artist");
        if (string.IsNullOrWhiteSpace(title)) return false;

        var id = uri != null ? ExtractTrackId(uri) : null;
        id ??= SynthId(artist ?? string.Empty, title, GetString(el, "albumName") ?? GetString(el, "album") ?? string.Empty);

        var durationMs = 0;
        if (el.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number
            && d.TryGetInt32(out var ms))
            durationMs = ms;

        var row = new SpotifyTrackRow
        {
            SpotifyId = id,
            Title = title.Trim(),
            Artist = (artist ?? string.Empty).Trim(),
            Album = (GetString(el, "albumName") ?? GetString(el, "album") ?? string.Empty).Trim(),
            DurationMs = durationMs,
            ArtworkUrl = string.Empty,
            IsPlayable = true,
            AddedAt = DateTime.UtcNow.ToString("o")
        };
        return TryAdd(row, byId);
    }

    private static bool TryAdd(SpotifyTrackRow row, Dictionary<string, SpotifyTrackRow> byId)
    {
        if (string.IsNullOrEmpty(row.SpotifyId)) return false;
        if (byId.ContainsKey(row.SpotifyId)) return false;
        byId.Add(row.SpotifyId, row);
        return true;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>spotify:track:0eEgMbSzOHmkOeVuNC3E0k → 0eEgMbSzOHmkOeVuNC3E0k.</summary>
    private static string? ExtractTrackId(string? uriOrId)
    {
        if (string.IsNullOrWhiteSpace(uriOrId)) return null;
        var value = uriOrId.Trim();
        const string prefix = "spotify:track:";
        var idx = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) value = value[(idx + prefix.Length)..];
        else if (value.Contains(':')) return null; // spotify:episode: / spotify:local: / прочее

        value = value.Split('?', '/')[0];
        return value.Length is > 0 and <= 64 ? value : null;
    }

    /// <summary>Стабильный суррогатный ID для строк без URI: spotify_id — PRIMARY KEY.</summary>
    private static string SynthId(string artist, string title, string album)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            artist.Trim().ToLowerInvariant() + "\n" + title.Trim().ToLowerInvariant() + "\n" + album.Trim().ToLowerInvariant()));
        var sb = new StringBuilder(23).Append("imp_");
        for (var i = 0; i < 9; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static int ParseDurationMs(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();

        // "3:45" / "1:03:12" → мс
        if (value.Contains(':'))
        {
            if (TimeSpan.TryParseExact(value, new[] { @"hh\:mm\:ss", @"m\:ss" },
                    CultureInfo.InvariantCulture, out var time))
                return (int)time.TotalMilliseconds;
            return 0;
        }

        if (!int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            return 0;

        // Экспортёры пишут и секунды, и мс; треки короче 10 секунд не бывают.
        return num < 10000 ? num * 1000 : num;
    }
}
