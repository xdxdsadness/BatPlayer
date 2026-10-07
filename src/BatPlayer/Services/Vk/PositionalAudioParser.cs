using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>
/// One track extracted from a positional array of an al_audio.php response (the
/// vk.com music catalog web endpoint used by the VK web player). Some fields may be
/// missing — VK changes the layout; only vk_id and the presence of url are reliable.
/// </summary>
public sealed class ParsedWebAudio
{
    public long AudioId { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    /// <summary>Duration in seconds; 0 — could not be determined (position is unstable).</summary>
    public long DurationSec { get; init; }
    /// <summary>Temporary direct mp3/hls URL; null — track is hidden/deleted (unplayable).</summary>
    public string? StreamUrl { get; init; }
    /// <summary>Artwork URL (sun*.userapi.com/*.jpg); null — the UI shows a placeholder.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>Hidden/deleted entries (no url) are unplayable and excluded from the catalog.</summary>
    public bool IsPlayable => !string.IsNullOrEmpty(StreamUrl);

    /// <summary>vk_id = "{owner_id}_{audio_id}" (same format audio.get used).</summary>
    public string VkId => $"{OwnerId}_{AudioId}";
}

/// <summary>
/// Result of parsing an al_audio.php response body.
/// </summary>
internal sealed record AlAudioPayload(
    IReadOnlyList<ParsedWebAudio> Tracks,
    /// <summary>Explicit nextOffset from the payload (VK does not always send it); null — none.</summary>
    int? NextOffset,
    /// <summary>Reported section total; 0 — not found in the payload.</summary>
    int ReportedTotal,
    /// <summary>Error code from payload[0].code (0 — success); non-zero — VK returned a section error.</summary>
    int ErrorCode,
    /// <summary>At least one JSON chunk parsed; false — the response is not al_audio at all (garbage/HTML).</summary>
    bool Parsed);

/// <summary>
/// Parses responses of the internal al_audio.php web endpoint. The response is text of
/// the form `<!json>{"payload":[{"code":0,"data":[...]}]}` where data holds POSITIONAL
/// track arrays. VK changes the layout regularly, so parsing is lenient:
///
///   • a track candidate is an array of length ≥ <see cref="MinTrackArrayLength"/> whose
///     [0] is an integer &gt; 0 (audio id), [1] a non-zero integer (owner_id; negative
///     for group posts), and whose top-level items include an "http…" string;
///   • the whole payload tree is scanned recursively (the track list is usually data[1],
///     but the wrapper position changes too) — short service arrays fail the filter;
///   • from string items: the first http non-image is the stream (mp3/hls), the first
///     http image (.jpg/.png/…) is the artwork (if absent at the top level, a shallow
///     search runs through nested arrays — artwork lives in element[13]-like slots);
///   • artist/title — the first two non-http strings (VK always emits artist before
///     title on all known layouts); a single string is split on "Artist - Title";
///   • duration — the first integer after the title position in range 1..86400.
///
/// On the first successful parse one raw element (truncated to 500 chars) is logged to
/// diagnose the live layout. Cookies/tokens never reach the logs (the track payload
/// contains none).
/// </summary>
/// <summary>A track from the modern type=recent section (tuple arrays).</summary>
public sealed class RecentAudioTuple
{
    public long AudioId { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public long DurationSec { get; init; }
    /// <summary>Stream URL hash: reload_audio builds the mp3 URL from it.</summary>
    public string UrlHash { get; init; } = string.Empty;
    /// <summary>Comma-separated artwork URLs (sun*.vkuserphoto.ru); empty — placeholder.</summary>
    public string ArtworkUrls { get; init; } = string.Empty;
}

internal static class PositionalAudioParser
{
    /// <summary>Minimum length of a track candidate array (real items have ≥ 12 fields).</summary>
    internal const int MinTrackArrayLength = 12;

    /// <summary>Sane upper bound for duration (sec) — filters out ids/flags during the search.</summary>
    private const long MaxDurationSec = 86400;

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    private static bool _rawLogged;

    // ========================= Response body =========================

       /// <summary>
    /// Full parse of an al_audio.php response body (possibly several `&lt;!json&gt;` chunks).
    /// Garbage/HTML never throws — an empty result is returned; an expired session is
    /// caught earlier by <see cref="IsLoginHtml"/>.
    /// </summary>
    internal static AlAudioPayload ParsePayload(string body)
    {
        var tracks = new List<ParsedWebAudio>();
        int? nextOffset = null;
        int total = 0;
        int errorCode = 0;
        var parsed = false;

        foreach (var chunk in SplitJsonChunks(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(chunk);
                parsed = true;
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload)
                    && payload.ValueKind == JsonValueKind.Array)
                {
                    // Section error codes are taken only from top-level payload items
                    // ({"code":N,"data":[...]}); nested "code" fields of other objects are ignored.
                    foreach (var item in payload.EnumerateArray())
                    {
                        // The audio auth challenge arrives as a status STRING: {"payload":["3",[...]]}.
                        if (item.ValueKind == JsonValueKind.String
                            && item.GetString() is { } status && status != "0")
                        {
                            if (int.TryParse(status, out var sc) && errorCode == 0)
                                errorCode = sc;
                            continue;
                        }
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (item.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c != 0)
                        {
                            errorCode = errorCode == 0 ? c : errorCode;
                        }
                    }
                }

                CollectTracks(root, tracks);
                CollectNumbers(root, ref nextOffset, ref total);
            }
            catch (JsonException ex)
            {
                // Chunk is not JSON (truncated/garbage) — try the remaining chunks anyway.
                Logger.Error(ex, "VK al_audio JSON chunk parse failed");
            }
        }

        return new AlAudioPayload(tracks, nextOffset, total, errorCode, parsed);
    }

    /// <summary>
    /// VK returned a login page (HTML with a sign-in form) — web session cookies expired.
    /// An empty response does not count as a login page (that is a transport issue, not a session one).
    /// </summary>
    internal static bool IsLoginHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("<!json>", StringComparison.Ordinal)) return false;
        if (!trimmed.StartsWith('<')) return false;

        return trimmed.Contains("login", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits the body into JSON chunks: each segment after `&lt;!json&gt;` starting with '{'
    /// (bounds up to the last '}', trailing garbage trimmed). Without the marker, the body
    /// counts as one chunk only if it starts with '{'.
    /// </summary>
    private static IEnumerable<string> SplitJsonChunks(string body)
    {
        const string marker = "<!json>";
        var segments = body.Contains(marker, StringComparison.Ordinal)
            ? body.Split(marker)
            : [body];

        foreach (var segment in segments)
        {
            var start = segment.IndexOf('{');
            var end = segment.LastIndexOf('}');
            if (start < 0 || end <= start) continue;
            yield return segment[start..(end + 1)];
        }
    }

    // ====================== Track array search ====================

    /// <summary>DFS over the JSON tree: candidates are collected, their subtrees are not traversed.</summary>
    private static void CollectTracks(JsonElement element, List<ParsedWebAudio> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                if (IsTrackCandidate(element))
                {
                    var track = ParseTrackElement(element);
                    if (track != null)
                    {
                        into.Add(track);
                        return; // no tracks inside a track — do not traverse
                    }
                }
                foreach (var child in element.EnumerateArray())
                    CollectTracks(child, into);
                break;

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    CollectTracks(prop.Value, into);
                break;
        }
    }

    /// <summary>
    /// nextOffset/total are searched anywhere in the payload (VK puts them in service
    /// data objects — the position changes). The first values found are used.
    /// </summary>
    private static void CollectNumbers(JsonElement element, ref int? nextOffset, ref int total)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;

                    if (nextOffset == null
                        && string.Equals(prop.Name, "nextOffset", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.TryGetInt32(out var next) && next >= 0)
                    {
                        nextOffset = next;
                    }
                    else if (total == 0
                        && string.Equals(prop.Name, "total", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.TryGetInt32(out var t) && t > 0)
                    {
                        total = t;
                    }
                }
                foreach (var prop in element.EnumerateObject())
                    CollectNumbers(prop.Value, ref nextOffset, ref total);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectNumbers(child, ref nextOffset, ref total);
                break;
        }
    }

    /// <summary>Track candidate: length ≥ 12, [0] an integer &gt; 0, [1] a non-zero integer, has an http string.</summary>
    internal static bool IsTrackCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return false;
        if (element.GetArrayLength() < MinTrackArrayLength) return false;

        if (!TryGetArrayInt(element, 0, out var audioId) || audioId <= 0) return false;
        if (!TryGetArrayInt(element, 1, out var ownerId) || ownerId == 0) return false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && IsHttpString(item.GetString()))
                return true;
        }
        return false;
    }

    private static bool TryGetArrayInt(JsonElement array, int index, out long value)
    {
        value = 0;
        if (array.ValueKind != JsonValueKind.Array) return false;
        var length = array.GetArrayLength();
        if (index < 0 || index >= length) return false;
        var item = array[index];
        if (item.ValueKind != JsonValueKind.Number) return false;
        return item.TryGetInt64(out value);
    }

    // ===================== Track fields (lenient) ====================

    /// <summary>
    /// Fields extracted per the rules in the type's doc summary. null — candidate did not match (zero ids).
    /// </summary>
    internal static ParsedWebAudio? ParseTrackElement(JsonElement element)
    {
        if (!TryGetArrayInt(element, 0, out var audioId) || audioId <= 0) return null;
        if (!TryGetArrayInt(element, 1, out var ownerId) || ownerId == 0) return null;

        string? streamUrl = null;
        string? artworkUrl = null;
        var texts = new List<(int index, string value)>();
        var titleIndex = -1;

        var index = -1;
        foreach (var item in element.EnumerateArray())
        {
            index++;
            if (index < 2) continue; // [0]/[1] — identifiers

            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (string.IsNullOrEmpty(s)) continue;

                if (IsHttpString(s))
                {
                    if (IsImageUrl(s))
                        artworkUrl ??= s;
                    else
                        streamUrl ??= s;
                }
                else
                {
                    texts.Add((index, s));
                }
            }
        }

        string artist, title;
        if (texts.Count >= 2)
        {
            // VK always emits artist before title (positions 2/3 or 3/4 on known layouts).
            artist = texts[0].value;
            title = texts[1].value;
            titleIndex = texts[1].index;
        }
        else if (texts.Count == 1)
        {
            // Sometimes VK merges "Artist - Title" (subtitle mode) — split on the first " - ".
            var dash = texts[0].value.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = texts[0].value[..dash];
                title = texts[0].value[(dash + 3)..];
            }
            else
            {
                artist = string.Empty;
                title = texts[0].value;
            }
            titleIndex = texts[0].index;
        }
        else
        {
            // Unusual layout: titles not at the top level — leave empty; the raw element
            // is logged (see LogRawElement) for layout analysis.
            artist = string.Empty;
            title = string.Empty;
        }

        // duration — the first integer right after the title position (a neighboring field
        // on known layouts); with an unknown position, search from index 2.
        var duration = 0L;
        var searchFrom = titleIndex >= 0 ? titleIndex + 1 : 2;
        for (var i = searchFrom; i < element.GetArrayLength(); i++)
        {
            if (TryGetArrayInt(element, i, out var v) && v > 0 && v <= MaxDurationSec)
            {
                duration = v;
                break;
            }
        }

        // Artwork not at the top level — shallow search in nested arrays
        // (element[13]-like): track nesting holds album/artists.
        if (artworkUrl == null)
            artworkUrl = FindNestedImage(element, depth: 0);

        LogRawElement(element);

        return new ParsedWebAudio
        {
            AudioId = audioId,
            OwnerId = ownerId,
            Title = title,
            Artist = artist,
            DurationSec = duration,
            StreamUrl = streamUrl,
            ArtworkUrl = artworkUrl
        };
    }

    private static string? FindNestedImage(JsonElement element, int depth)
    {
        if (depth > 3) return null;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String
                    && IsHttpString(child.GetString())
                    && IsImageUrl(child.GetString()))
                {
                    return child.GetString();
                }
                var nested = FindNestedImage(child, depth + 1);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var nested = FindNestedImage(prop.Value, depth + 1);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static bool IsHttpString(string? s)
        => !string.IsNullOrEmpty(s)
           && s.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks like an image (extension before '?'; mp3/m3u8 stream URLs are excluded).</summary>
    private static bool IsImageUrl(string? s)
    {
        if (IsHttpString(s) == false) return false;

        var query = s!.IndexOf('?');
        var path = query >= 0 ? s[..query] : s;
        foreach (var ext in ImageExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Live layout diagnostics: once per process, logs the raw element (up to 500 chars)
    /// of the first parsed track; field positions are clearly visible in it.
    /// </summary>
    private static void LogRawElement(JsonElement element)
    {
        if (_rawLogged) return;
        _rawLogged = true;

        try
        {
            var raw = element.GetRawText();
            if (raw.Length > 500) raw = raw[..500] + "…";
            Logger.Info($"VK al_audio track layout sample: {raw}");
        }
        catch (Exception ex)
        {
            // Diagnostics must not break parsing.
            Logger.Error(ex, "VK al_audio raw element logging failed");
        }
    }

    /// <summary>
    /// Parses the modern type=recent section: payload[0] is the code (0 = success),
    /// payload[1][0] is the section object whose "list" holds track tuples:
    ///   [0]=id, [1]=owner_id, [3]=title, [4]=artist, [5]=duration, [13]=url hash,
    ///   [14]=comma-separated artwork URLs.
    /// Garbage/unexpected layout — empty result without exceptions.
    /// </summary>
    internal static (int ErrorCode, List<RecentAudioTuple> Tracks) ParseRecentSection(string? body)
    {
        var tracks = new List<RecentAudioTuple>();
        var errorCode = 0;

        if (string.IsNullOrWhiteSpace(body)) return (0, tracks);

        var json = body.Trim();
        var jsonStart = json.IndexOf("<!json>", StringComparison.Ordinal);
        if (jsonStart >= 0) json = json[(jsonStart + 7)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Array
                || payload.GetArrayLength() < 2)
            {
                return (0, tracks);
            }

            // Status code: a number or numeric string in payload[0]
            var status = payload[0];
            if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code != 0)
                errorCode = code;
            else if (status.ValueKind == JsonValueKind.String
                     && int.TryParse(status.GetString(), out var sc) && sc != 0)
                errorCode = sc;

            if (errorCode != 0) return (errorCode, tracks);

            var data = payload[1];
            if (data.ValueKind != JsonValueKind.Array) return (0, tracks);

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array) continue;

                foreach (var tuple in list.EnumerateArray())
                {
                    if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 6) continue;

                    long audioId = tuple[0].ValueKind == JsonValueKind.Number ? tuple[0].GetInt64() : 0;
                    long ownerId = tuple[1].ValueKind == JsonValueKind.Number ? tuple[1].GetInt64() : 0;
                    var title = tuple[3].ValueKind == JsonValueKind.String ? tuple[3].GetString() ?? "" : "";
                    var artist = tuple[4].ValueKind == JsonValueKind.String ? tuple[4].GetString() ?? "" : "";
                    long duration = tuple[5].ValueKind == JsonValueKind.Number ? tuple[5].GetInt64() : 0;
                    var urlHash = tuple.GetArrayLength() > 13 && tuple[13].ValueKind == JsonValueKind.String
                        ? tuple[13].GetString() ?? "" : "";
                    var covers = tuple.GetArrayLength() > 14 && tuple[14].ValueKind == JsonValueKind.String
                        ? tuple[14].GetString() ?? "" : "";

                    if (audioId == 0 || string.IsNullOrEmpty(title)) continue;

                    tracks.Add(new RecentAudioTuple
                    {
                        AudioId = audioId,
                        OwnerId = ownerId,
                        Title = title,
                        Artist = artist,
                        DurationSec = Math.Max(0, duration),
                        UrlHash = urlHash,
                        ArtworkUrls = covers
                    });
                }
            }
        }
        catch (JsonException)
        {
            // garbage/truncation — empty result
        }

        return (errorCode, tracks);
    }
}
