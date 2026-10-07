using System.Text.Json;

namespace BatPlayer.Services.YandexMusic;

/// <summary>Yandex Music layer error after all retries. HttpCode — response code (0 — transport/parsing,
/// 401/403 — session (OAuth token) invalid).</summary>
public sealed class YmApiException : Exception
{
    public int HttpCode { get; }

    public YmApiException(string message, int httpCode = 0) : base(message) => HttpCode = httpCode;

    /// <summary>The code means the OAuth token is revoked/invalid.</summary>
    public static bool IsSessionError(int httpCode) => httpCode is 401 or 403;
}

/// <summary>Track from API JSON responses (likes→tracks, landing). Some fields may be missing —
/// parsing is lenient; id and title are reliable.</summary>
public sealed class YmTrackDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    /// <summary>Artists joined by ", " (up to three names, like the web player).</summary>
    public string Artist { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    /// <summary>Cover template with "%%" instead of the size (albums[].coverUri); null — no cover.</summary>
    public string? CoverUri { get; init; }
    /// <summary>Track available on the current plan (field available; a missing field is treated as available).</summary>
    public bool Available { get; init; } = true;
}

/// <summary>Artist from API JSON responses (search, similar artists). Only id is reliable.</summary>
public sealed class YmArtistDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>Account data from account/status (uid + display name).</summary>
public sealed class YmAccountInfo
{
    public string Uid { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>A ready direct mp3 URL variant from download-info (URL already built by the parser).</summary>
public sealed class YmDownloadOption
{
    public string Codec { get; init; } = string.Empty;
    public int Bitrate { get; init; }
    public string Url { get; init; } = string.Empty;

    /// <summary>URL of the XML descriptor (storage.mds.yandex.net/…/download-info) from
    /// which the final mp3 URL is built — modern API layout. Empty — the variant already
    /// carries a ready Url (legacy layouts).</summary>
    public string DownloadInfoUrl { get; init; } = string.Empty;
}

/// <summary>Fields of the download-info XML descriptor: host/path/ts/s → final URL
/// https://{host}/get-mp3/{s}/{ts}{path}.</summary>
public sealed class YmDownloadInfoXml
{
    public string Host { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Ts { get; init; } = string.Empty;
    public string S { get; init; } = string.Empty;
}

/// <summary>Device code from POST oauth.yandex.ru/device-code (OAuth Device Flow).</summary>
public sealed class YmDeviceCode
{
    public string DeviceCode { get; init; } = string.Empty;
    public string UserCode { get; init; } = string.Empty;
    /// <summary>Verification page (oauth.yandex.ru/device); empty — the default is used.</summary>
    public string VerificationUrl { get; init; } = string.Empty;
    /// <summary>Seconds until the code expires (0 — the default is used).</summary>
    public int ExpiresInSeconds { get; init; }
    /// <summary>Recommended polling interval in seconds (0 — the default is used).</summary>
    public int IntervalSeconds { get; init; }
}

/// <summary>Response of POST oauth.yandex.ru/token: either a token or an OAuth error code
/// (authorization_pending / slow_down / expired_token / access_denied / …).</summary>
public sealed class YmTokenResult
{
    public string AccessToken { get; init; } = string.Empty;
    public int? ExpiresInSeconds { get; init; }
    /// <summary>OAuth error code from the 400 body; null — no error (token obtained).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The user has not approved the code yet — keep polling.</summary>
    public bool IsPending => string.Equals(ErrorCode, "authorization_pending", StringComparison.Ordinal);

    /// <summary>Yandex asked to poll less often (poll_interval+5 s).</summary>
    public bool IsSlowDown => string.Equals(ErrorCode, "slow_down", StringComparison.Ordinal);

    /// <summary>The code expired — a new one is needed.</summary>
    public bool IsExpired => string.Equals(ErrorCode, "expired_token", StringComparison.Ordinal);

    /// <summary>The user denied consent / the client was rejected — polling is pointless.</summary>
    public bool IsDenied => ErrorCode is "access_denied" or "invalid_client" or "unauthorized_client"
                            or "invalid_grant" or "bad_verification_code";
}

/// <summary>
/// Parses Yandex Music API JSON responses. Kept in a separate static class: the
/// api.music.yandex.net endpoints are undocumented and change, so all parsing is lenient
/// (garbage/unexpected layout yields an empty result instead of exceptions), isolated
/// from the network and unit-tested on realistic fixtures. Track responses contain no
/// cookies/tokens — logging parsed data is safe.
/// </summary>
public static class YmJsonParser
{
    // ========================= Tracks (tracks?track-ids) =========================

    /// <summary>
    /// Response of GET /tracks?track-ids=…&amp;lang=ru: {"result":[{…track…}, …]}.
    /// The result order matches the requested id order (unavailable ids are absent).
    /// </summary>
    public static List<YmTrackDto> ParseTracksResponse(string? json)
    {
        var result = new List<YmTrackDto>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("result", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var track = ParseTrackObject(item);
                if (track != null) result.Add(track);
            }
        }
        return result;
    }

    // ========================= Landing (blocks) =========================

    /// <summary>
    /// Response of GET /landing?types=track&amp;lang=ru: {"result":{"blocks":[{…, "tracks":[…]}]}}.
    /// The block layout changes — tracks are collected by DFS over the whole tree: a track
    /// candidate is an object with "id", a string "title" and an "artists" or "albums"
    /// array; id duplicates are dropped (a track may appear in several blocks).
    /// </summary>
    public static List<YmTrackDto> ParseLandingTracks(string? json)
    {
        var result = new List<YmTrackDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = TryParse(json);
        if (doc == null) return result;

        CollectTrackObjects(doc.RootElement, result, seen);
        return result;
    }

    private static void CollectTrackObjects(JsonElement element, List<YmTrackDto> into, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Track candidate: object with id+title AND at least one platform
                // collection (artists/albums). Without this filter the DFS would also
                // catch albums (they have id+title too) and block service objects.
                if ((element.TryGetProperty("artists", out _) || element.TryGetProperty("albums", out _))
                    && ParseTrackObject(element) is { } track)
                {
                    if (seen.Add(track.Id)) into.Add(track);
                    return; // do not descend into a track object
                }
                foreach (var prop in element.EnumerateObject())
                    CollectTrackObjects(prop.Value, into, seen);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectTrackObjects(child, into, seen);
                break;
        }
    }

    // ========================= Similar (tracks/{id}/similar) =========================

    /// <summary>
    /// Response of GET /tracks/{id}/similar: {"result":[{…seed…, "similarTracks":[…track…]}]}
    /// (the wrapper {"result":{"similarTracks":[…]}} also occurs). Layout quirk: candidates
    /// live INSIDE the seed track object, so the landing DFS does not fit — it does not
    /// descend into track objects. Here an object with "similarTracks" yields only that
    /// array's contents (the seed itself is not collected) and the rest of the tree is
    /// walked recursively; inside the array, candidates are parsed by the regular DFS.
    /// Id duplicates are dropped and the seed (seedYmId) is excluded. An empty list —
    /// no match/no similar tracks.
    /// </summary>
    public static List<YmTrackDto> ParseSimilarTracks(string? json, string? seedYmId)
    {
        var result = new List<YmTrackDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = TryParse(json);
        if (doc == null) return result;

        CollectSimilarTrackObjects(doc.RootElement, result, seen);

        if (!string.IsNullOrEmpty(seedYmId))
            result.RemoveAll(t => string.Equals(t.Id, seedYmId, StringComparison.Ordinal));

        return result;
    }

    private static void CollectSimilarTrackObjects(JsonElement element, List<YmTrackDto> into, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // similarTracks array: candidates are regular track objects (artists/albums
                // required — see CollectTrackObjects). The wrapper object itself (seed and
                // service fields) is not collected.
                if (element.TryGetProperty("similarTracks", out var similar)
                    && similar.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in similar.EnumerateArray())
                        CollectTrackObjects(child, into, seen);
                    return;
                }
                foreach (var prop in element.EnumerateObject())
                    CollectSimilarTrackObjects(prop.Value, into, seen);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectSimilarTrackObjects(child, into, seen);
                break;
        }
    }

    // ========================= Search (/search) =========================

    /// <summary>
    /// Response of GET /search?text=…&amp;type=track: {"result":{"tracks":{"results":[…track…],
    /// "total":…}, …}}. First the exact path result.tracks.results; if the layout does not
    /// match — DFS fallback over the whole tree (same as similar). Pure function, unit-tested.
    /// </summary>
    public static List<YmTrackDto> ParseSearchTracks(string? json)
    {
        var result = new List<YmTrackDto>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("tracks", out var tracks)
            && tracks.ValueKind == JsonValueKind.Object
            && tracks.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (ParseTrackObject(item) is { } track) result.Add(track);
            }
            if (result.Count > 0) return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectTrackObjects(root, result, seen);
        return result;
    }

    /// <summary>
    /// Response of GET /search?text=…&amp;type=artist: {"result":{"artists":{"results":[…artist…]}}}.
    /// Exact path result.artists.results; fallback — DFS over artist objects
    /// (id + name + an artist marker: cover/various/composer/genres — filters out labels
    /// sharing the same id+name pair). Pure function, unit-tested.
    /// </summary>
    public static List<YmArtistDto> ParseSearchArtists(string? json)
    {
        var result = new List<YmArtistDto>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("artists", out var artists)
            && artists.ValueKind == JsonValueKind.Object
            && artists.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (ParseArtistObject(item) is { } artist) result.Add(artist);
            }
            if (result.Count > 0) return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectArtistObjects(root, result, seen);
        return result;
    }

    // ========================= Artists (artists/{id}/…) =========================

    /// <summary>
    /// Response of GET /artists/{id}/tracks?page=…&amp;pageSize=…:
    /// {"result":{"pager":{…},"tracks":[…track…]}} — the artist's catalog tracks,
    /// NOT the user's likes. Exact path result.tracks; fallback — DFS over the tree.
    /// Pure function, unit-tested.
    /// </summary>
    public static List<YmTrackDto> ParseArtistTracks(string? json)
    {
        var result = new List<YmTrackDto>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("tracks", out var tracks)
            && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tracks.EnumerateArray())
            {
                if (ParseTrackObject(item) is { } track) result.Add(track);
            }
            if (result.Count > 0) return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectTrackObjects(root, result, seen);
        return result;
    }

    /// <summary>
    /// Response of GET /artists/{id}/brief-info: {"result":{"stats":{"lastMonthListeners":…},…}} —
    /// the artist's monthly audience (as shown on the artist card). Falls back to
    /// "listeners" in case the API changes. Used to filter no-names/"AI tracks".
    /// null — field absent (unknown). Pure function, unit-tested.
    /// </summary>
    public static long? ParseArtistListeners(string? json)
    {
        using var doc = TryParse(json);
        if (doc == null) return null;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var res)
            || res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("stats", out var stats)
            || stats.ValueKind != JsonValueKind.Object)
            return null;

        // Current API field — lastMonthListeners (the card's monthly audience).
        if (stats.TryGetProperty("lastMonthListeners", out var month)
            && month.ValueKind == JsonValueKind.Number
            && month.TryGetInt64(out var monthValue))
            return monthValue;

        // Fallback: the field's former name.
        if (stats.TryGetProperty("listeners", out var listeners)
            && listeners.ValueKind == JsonValueKind.Number
            && listeners.TryGetInt64(out var value))
            return value;

        return null;
    }

    /// <summary>
    /// Response of GET /artists/{id}/similar: {"result":{"artist":{…},
    /// "similarArtists":[…artist…]}} — similar artists ("same scene"): the array
    /// elements are the artist objects themselves. Exact path result.similarArtists;
    /// fallback — DFS over artist objects. Pure function, unit-tested.
    /// </summary>
    public static List<YmArtistDto> ParseSimilarArtists(string? json, string? artistId)
    {
        var result = new List<YmArtistDto>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        var found = false;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("similarArtists", out var similar)
            && similar.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in similar.EnumerateArray())
            {
                if (ParseArtistObject(item) is { } artist) result.Add(artist);
            }
            found = true;
        }

        if (!found)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            CollectArtistObjects(root, result, seen);
        }

        if (!string.IsNullOrEmpty(artistId))
            result.RemoveAll(a => string.Equals(a.Id, artistId, StringComparison.Ordinal));

        return result;
    }

    /// <summary>
    /// Response of GET /rotor/station/artist:{id}/tracks: {"result":{"sequence":[
    /// {"__type":"track","track":{…track…}}, …], "batchId":…}} — a personal radio feed
    /// for the artist ("similar sound"). Tracks are collected by DFS over the tree
    /// (candidate — an object with id+title+artists/albums); duplicates are dropped.
    /// Pure function, unit-tested.
    /// </summary>
    public static List<YmTrackDto> ParseRadioTracks(string? json)
    {
        var result = new List<YmTrackDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = TryParse(json);
        if (doc == null) return result;

        CollectTrackObjects(doc.RootElement, result, seen);
        return result;
    }

    /// <summary>
    /// Parses an artist object: has "id" (string/number) and a string "name".
    /// null — no match.
    /// </summary>
    public static YmArtistDto? ParseArtistObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var id = GetStringOrNumber(element, "id");
        var name = GetString(element, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;

        return new YmArtistDto { Id = id, Name = name };
    }

    /// <summary>
    /// DFS collection of artist objects over the tree: id + name + at least one artist
    /// marker (cover/various/composer/genres) — filters out labels and other objects
    /// with an id+name pair. Id duplicates are dropped.
    /// </summary>
    private static void CollectArtistObjects(JsonElement element, List<YmArtistDto> into, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("name", out _)
                    && (element.TryGetProperty("cover", out _)
                        || element.TryGetProperty("various", out _)
                        || element.TryGetProperty("composer", out _)
                        || element.TryGetProperty("genres", out _))
                    && ParseArtistObject(element) is { } artist)
                {
                    if (seen.Add(artist.Id)) into.Add(artist);
                    return;
                }
                foreach (var prop in element.EnumerateObject())
                    CollectArtistObjects(prop.Value, into, seen);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectArtistObjects(child, into, seen);
                break;
        }
    }

    // ========================= Likes (users/{uid}/likes/tracks) =========================

    /// <summary>
    /// Response of GET /users/{uid}/likes/tracks: {"result":{"library":{"tracks":[{"id":…,"timestamp":…}, …]}}}.
    /// IMPORTANT: likes return ONLY ids — full objects are fetched in batches via /tracks.
    /// The id may be a number or a string. If the library wrapper is unexpectedly missing —
    /// DFS fallback over objects with id+timestamp fields (the layout changes).
    /// </summary>
    /// <summary>A like entry: track id (plus a possible suffix) and the like time from the API
    /// (ISO 8601, e.g. "2026-09-30T15:20:20+00:00"; empty — the server did not send it).</summary>
    public sealed record YmLikeEntry(string Id, string LikedAt);

    /// <summary>
    /// Likes with timestamps: id + entry timestamp. The main layout is
    /// {"result":{"library":{"tracks":[{id, timestamp}, …]}}}; fallback — a search over
    /// objects with id+timestamp fields (the layout changes).
    /// </summary>
    public static List<YmLikeEntry> ParseLikeEntries(string? json)
    {
        var result = new List<YmLikeEntry>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var res)
            && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("library", out var library)
            && library.ValueKind == JsonValueKind.Object
            && library.TryGetProperty("tracks", out var tracks)
            && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tracks.EnumerateArray())
            {
                var id = GetStringOrNumber(item, "id");
                if (!string.IsNullOrEmpty(id))
                    result.Add(new YmLikeEntry(id, GetStringOrNumber(item, "timestamp") ?? string.Empty));
            }
            return result;
        }

        // Fallback: search for id+timestamp pairs across the whole tree.
        CollectLikeEntries(root, result);
        return result;
    }

    /// <summary>Like ids only (no timestamps) — a wrapper over <see cref="ParseLikeEntries"/>.</summary>
    public static List<string> ParseLikeIds(string? json)
        => ParseLikeEntries(json).Select(e => e.Id).ToList();

    private static void CollectLikeEntries(JsonElement element, List<YmLikeEntry> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var id = GetStringOrNumber(element, "id");
                if (!string.IsNullOrEmpty(id)
                    && element.TryGetProperty("timestamp", out var ts))
                {
                    var tsText = ts.ValueKind == JsonValueKind.String ? ts.GetString() : ts.GetRawText();
                    into.Add(new YmLikeEntry(id, tsText ?? string.Empty));
                    return;
                }
                foreach (var prop in element.EnumerateObject())
                    CollectLikeEntries(prop.Value, into);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectLikeEntries(child, into);
                break;
        }
    }

    // ========================= Account (account/status) =========================

    /// <summary>
    /// Response of GET /account/status: {"result":{"account":{"uid":…,"login":…,"fullName":…,
    /// "displayName":{"name":…}, …}}}. Name priority: fullName → displayName.name → login.
    /// null — layout did not match (not an account response).
    /// </summary>
    public static YmAccountInfo? ParseAccountStatus(string? json)
    {
        using var doc = TryParse(json);
        if (doc == null) return null;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var res)
            || res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var uid = GetStringOrNumber(account, "uid");
        if (string.IsNullOrEmpty(uid)) return null;

        var name = GetString(account, "fullName")
                   ?? (account.TryGetProperty("displayName", out var dn)
                       && dn.ValueKind == JsonValueKind.Object
                       ? GetString(dn, "name")
                       : null)
                   ?? GetString(account, "login")
                   ?? string.Empty;

        return new YmAccountInfo { Uid = uid, DisplayName = name };
    }

    // ========================= Download-info =========================

    /// <summary>
    /// Response of GET /tracks/{id}/download-info. The variant layout changed several times —
    /// parsing is lenient; for each result[] item a URL is attempted:
    ///   0) the "downloadInfoUrl" field (2025+ layout, bitrateInKbps field) — an XML
    ///      descriptor URL; the final URL is built by the service via ParseDownloadInfoXml;
    ///   1) a "urls" array with direct links (transports=encode_info_websonic,pure_d);
    ///   2) the "url" field: a direct https link OR a "$" template — "$" is expanded via
    ///      the same item's host/path ("$host$path" → https://host + path);
    ///   3) the classic {host, path, ts, s} scheme → "https://{host}/get-mp3/{s}/{ts}{path}".
    /// Items without a computable URL/descriptor are skipped (lenient contract).
    /// </summary>
    public static List<YmDownloadOption> ParseDownloadInfo(string? json)
    {
        var result = new List<YmDownloadOption>();
        using var doc = TryParse(json);
        if (doc == null) return result;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var codec = GetString(item, "codec") ?? string.Empty;
            var bitrate = TryGetInt(item, "bitrateInKbps") ?? TryGetInt(item, "bitrate") ?? 0;

            // 0) Modern layout: a descriptor instead of a ready URL.
            var descriptor = GetString(item, "downloadInfoUrl");
            if (!string.IsNullOrWhiteSpace(descriptor))
            {
                result.Add(new YmDownloadOption
                {
                    Codec = codec,
                    Bitrate = bitrate,
                    DownloadInfoUrl = descriptor
                });
                continue;
            }

            var url = BuildDownloadUrl(item);
            if (string.IsNullOrEmpty(url)) continue;

            result.Add(new YmDownloadOption
            {
                Codec = codec,
                Bitrate = bitrate,
                Url = url
            });
        }
        return result;
    }

    /// <summary>
    /// XML descriptor at the downloadInfoUrl:
    /// &lt;download-info&gt;&lt;host&gt;…&lt;/host&gt;&lt;path&gt;…&lt;/path&gt;&lt;ts&gt;…&lt;/ts&gt;&lt;s&gt;…&lt;/s&gt;&lt;/download-info&gt;.
    /// null — garbage/missing fields. Pure function, unit-tested.
    /// </summary>
    public static YmDownloadInfoXml? ParseDownloadInfoXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "download-info") return null;

            string? Get(string name)
                => root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

            var host = Get("host");
            var path = Get("path");
            var ts = Get("ts");
            var s = Get("s");
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path)
                || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(s))
            {
                return null;
            }
            return new YmDownloadInfoXml { Host = host, Path = path, Ts = ts, S = s };
        }
        catch (System.Xml.XmlException)
        {
            return null; // not XML (proxy stub/truncation) — lenient
        }
    }

    /// <summary>Final mp3 URL from descriptor fields: https://{host}/get-mp3/{s}/{ts}{path}.</summary>
    public static string BuildDownloadUrlFromXml(YmDownloadInfoXml info)
    {
        var slash = info.Path.StartsWith('/') ? string.Empty : "/";
        return $"https://{info.Host}/get-mp3/{info.S}/{info.Ts}{slash}{info.Path}";
    }

    /// <summary>Builds a URL from one download-info item; null — no match (see the ParseDownloadInfo docs).</summary>
    internal static string? BuildDownloadUrl(JsonElement item)
    {
        var host = GetString(item, "host");
        var path = GetString(item, "path");

        // 1) Array of ready URLs "urls".
        if (item.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in urls.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.String) continue;
                var raw = u.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var expanded = ExpandPlaceholders(raw ?? "", host, path);
                if (!string.IsNullOrEmpty(expanded)) return expanded;
            }
        }

        // 2) Single "url" field: a direct link or a "$" template.
        var urlField = GetString(item, "url");
        if (!string.IsNullOrWhiteSpace(urlField))
        {
            var expanded = ExpandPlaceholders(urlField, host, path);
            if (!string.IsNullOrEmpty(expanded)) return expanded;
        }

        // 3) Classic {host, path, ts, s} scheme → https://{host}/get-mp3/{s}/{ts}{path}.
        var ts = GetString(item, "ts");
        var s = GetString(item, "s");
        if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(path)
            && !string.IsNullOrEmpty(ts) && !string.IsNullOrEmpty(s))
        {
            var slash = path!.StartsWith('/') ? string.Empty : "/";
            return $"https://{host}/get-mp3/{s}/{ts}{slash}{path}";
        }

        return null;
    }

    /// <summary>
    /// Expands a "$" template: "$host" → host, "$path" → path; a leftover single "$"
    /// is replaced with host+path as a whole. A scheme-less result gets "https://" prepended.
    /// </summary>
    private static string? ExpandPlaceholders(string url, string? host, string? path)
    {
        if (!url.Contains('$')) return NormalizeUrl(url);

        var hostPart = host ?? string.Empty;
        var pathPart = path ?? string.Empty;
        var expanded = url.Replace("$host", hostPart, StringComparison.Ordinal)
                          .Replace("$path", pathPart, StringComparison.Ordinal);
        if (expanded.Contains('$')) expanded = expanded.Replace("$", hostPart + pathPart, StringComparison.Ordinal);

        return NormalizeUrl(expanded);
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    // ========================= Track fields =========================

    /// <summary>
    /// Parses one track object. Track candidate: has "id" and a string "title".
    /// artist — the first up to three artists[].name joined by ", " (as in the Yandex
    /// web player); cover — albums[0].coverUri ("%%" template); missing available → true.
    /// </summary>
    public static YmTrackDto? ParseTrackObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var id = GetStringOrNumber(element, "id");
        var title = GetString(element, "title");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;

        return new YmTrackDto
        {
            Id = id,
            Title = title,
            Artist = JoinArtistNames(element),
            DurationMs = TryGetLong(element, "durationMs") ?? 0,
            CoverUri = GetFirstAlbumCoverUri(element),
            Available = TryGetBool(element, "available") ?? true
        };
    }

    /// <summary>Artist names joined by ", ", at most three (the Yandex web player does the same).</summary>
    public static string JoinArtistNames(JsonElement track)
    {
        if (!track.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var names = new List<string>();
        foreach (var a in artists.EnumerateArray())
        {
            var name = a.ValueKind == JsonValueKind.Object ? GetString(a, "name") : null;
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            if (names.Count == 3) break;
        }
        return string.Join(", ", names);
    }

    /// <summary>albums[0].coverUri ("%%" template instead of size); null — no cover.</summary>
    public static string? GetFirstAlbumCoverUri(JsonElement track)
    {
        if (!track.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var album in albums.EnumerateArray())
        {
            if (album.ValueKind != JsonValueKind.Object) continue;
            var uri = GetString(album, "coverUri");
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        return null;
    }

    /// <summary>
    /// Ready artwork URL from the coverUri template: "%%" is replaced with the size
    /// (e.g. "300x400"/"400x400"); a scheme-less URI gets "https://" prepended.
    /// No "%%" placeholder — the string is used as is (already a full URL).
    /// </summary>
    public static string? BuildArtworkUrl(string? coverUri, string size = "300x300")
    {
        if (string.IsNullOrWhiteSpace(coverUri)) return null;

        var url = coverUri.Contains("%%", StringComparison.Ordinal)
            ? coverUri.Replace("%%", size, StringComparison.Ordinal)
            : coverUri;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    // ========================= Primitives =========================

    // ========================= OAuth Device Flow ================================

    /// <summary>
    /// Body of the 200 response of POST oauth.yandex.ru/device-code:
    /// {"device_code":"…","user_code":"…","verification_url":"https://oauth.yandex.ru/device",
    ///  "expires_in":300,"interval":5}. null — body not recognized (network/garbage).
    /// Pure function, unit-tested.
    /// </summary>
    public static YmDeviceCode? ParseDeviceCode(string? json)
    {
        using var doc = TryParse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var deviceCode = GetString(doc.RootElement, "device_code");
        var userCode = GetString(doc.RootElement, "user_code");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode)) return null;

        return new YmDeviceCode
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUrl = GetString(doc.RootElement, "verification_url") ?? string.Empty,
            ExpiresInSeconds = TryGetInt(doc.RootElement, "expires_in") ?? 0,
            IntervalSeconds = TryGetInt(doc.RootElement, "interval") ?? 0
        };
    }

    /// <summary>
    /// Body of the POST oauth.yandex.ru/token response: 200 — {"access_token":"…"[,"expires_in":…]},
    /// 400 — {"error":"authorization_pending"|"slow_down"|…}. Non-JSON garbage (a proxy HTML
    /// stub etc.) → ErrorCode="invalid_response", so the window shows a general failure
    /// instead of waiting forever.
    /// Pure function, unit-tested.
    /// </summary>
    public static YmTokenResult ParseTokenResponse(string? json)
    {
        using var doc = TryParse(json);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return new YmTokenResult { ErrorCode = "invalid_response" };

        var accessToken = GetString(doc.RootElement, "access_token");
        if (!string.IsNullOrEmpty(accessToken))
        {
            return new YmTokenResult
            {
                AccessToken = accessToken,
                ExpiresInSeconds = TryGetInt(doc.RootElement, "expires_in")
            };
        }

        return new YmTokenResult { ErrorCode = GetString(doc.RootElement, "error") ?? "invalid_response" };
    }

    // ============================ Helpers =======================================

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null; // garbage/truncation — empty result, not an exception
        }
    }

    internal static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>String field the API returns both as a number and as a string (id, uid).</summary>
    internal static string? GetStringOrNumber(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    internal static int? TryGetInt(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i) ? i : null;

    internal static long? TryGetLong(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var l) ? l : null;

    internal static bool? TryGetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
