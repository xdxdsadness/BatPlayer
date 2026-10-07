using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Database;
using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>VK-layer error after all retries. Code — error code (0 — transport/parsing, 5 — session).</summary>
public sealed class VkApiException : Exception
{
    public int ErrorCode { get; }

    public VkApiException(string message, int errorCode = 0) : base(message) => ErrorCode = errorCode;
}

/// <summary>
/// VK Music client. Auth uses WEB-SESSION COOKIES of vk.com/vk.ru (same approach as the
/// SoundCloud integration): the login window collects cookies from WebView2, they are
/// stored in vk_auth.json, and the catalog comes from the internal al_audio.php web
/// endpoint (act=load_section) — the one the VK web player itself uses.
///
/// Why not OAuth: audio.get via third-party tokens is dead — VK returns codes 3/8 for
/// all third-party apps (including Kate Mobile); the token-based catalog is unavailable.
///
/// File downloads are NOT implemented: catalog only (metadata + temporary URLs),
/// streaming via the disk cache (VkStreamCache), and matching with the local library.
/// Network goes through <see cref="VkHttp"/>: direct first, system proxy as fallback.
/// Cookies never reach the logs — only method, response codes and item counts are logged.
/// </summary>
public sealed class VkService
{
    public const string AuthFileName = "vk_auth.json";

    /// <summary>
    /// Expected page size of load_section pagination. VK returns the "relevance" section
    /// in one response and does not always support offset; the constant backs the pagination
    /// guard when the payload shows a counter larger than what was collected.
    /// </summary>
    public const int PageSize = 1000;

    /// <summary>Guard against load_section pagination loops (10 × 1000 = 10k tracks).</summary>
    public const int MaxAudioPages = 10;

    /// <summary>How long a resolved mp3 URL lives in memory before the catalog is re-fetched.</summary>
    private static readonly TimeSpan StreamUrlTtl = TimeSpan.FromMinutes(20);

    /// <summary>Minimum interval between full URL-cache refreshes (after a failed resolve).</summary>
    private static readonly TimeSpan UrlRefreshCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Current VK domain (catalog web endpoint).</summary>
    internal const string WebAudioUrlRu = "https://vk.ru/al_audio.php";

    /// <summary>Legacy domain — fallback when .ru does not answer as al_audio.</summary>
    internal const string WebAudioUrlCom = "https://vk.com/al_audio.php";

    /// <summary>Referer expected by the web endpoint (the audio page).</summary>
    internal const string WebAudioReferer = "https://vk.ru/audio";

    /// <summary>Regular VK mobile login page (the login window opens it first).</summary>
    internal const string LoginUrl = "https://m.vk.com/login";

    /// <summary>UA for al_audio.php and the artwork CDN; internal — set by the VkHttp factory.</summary>
    /// <remarks>
    /// Uses the official VK Android app UA to bypass third-party client blocking.
    /// VK checks the UA and may serve a voice message instead of music when it detects
    /// a browser.
    /// </remarks>
    internal const string UserAgent =
        "VKAndroidApp/7.52-14788 (Android 13; SDK 33; arm64-v8a; Samsung SM-G998B; ru; 2400x1080)";

    /// <summary>Parallelism of the artwork batch download (see SyncArtworksAsync).</summary>
    private const int ArtworkDownloadParallelism = 4;

    private readonly VkAuthService _auth;
    private readonly VkArtworkCache _artworks = new();

    /// <summary>vk_id → temporary mp3 URL (from the last load_section). URLs are not written to the DB.</summary>
    private readonly Dictionary<string, (string Url, DateTime FetchedUtc)> _urlCache = new(StringComparer.Ordinal);

    /// <summary>URL cache lock (filled by the sync, read by the player resolver).</summary>
    private readonly object _urlLock = new();

    /// <summary>Full catalog fetches (URL refreshes) must not run in parallel.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private DateTime _lastUrlRefreshUtc = DateTime.MinValue;

    /// <summary>Sync progress: (done, estimated total).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    public VkService(string authFilePath) => _auth = new VkAuthService(authFilePath);

    /// <summary>Whether the session file exists (validity is not checked).</summary>
    public bool HasAuthFile => _auth.Exists;

    /// <summary>
    /// Whether the account is connected: the session file exists AND holds a non-empty
    /// cookie string. No network check is performed — an expired session surfaces on the
    /// first request.
    /// </summary>
    public bool HasWebSession => !string.IsNullOrWhiteSpace(_auth.Load().CookieHeader);

    /// <summary>VK user id from the session file ("" — unknown); for the settings status.</summary>
    public string GetUserId() => _auth.Load().UserId ?? string.Empty;

    // ============================ Session ============================

    /// <summary>
    /// Saves web session cookies after a successful sign-in (called by the login window).
    /// Cookies are never logged. AccessToken in the file is left untouched — the field
    /// exists for compatibility with older files.
    /// </summary>
    public void SaveSessionCookies(string cookieHeader, string? userId)
    {
        var file = _auth.Load();
        file.CookieHeader = cookieHeader;
        file.UserId = userId ?? string.Empty;
        file.SavedAt = DateTime.UtcNow.ToString("o");
        _auth.Save(file);
    }

    /// <summary>
    /// Marks the session invalid (VK returned a login page): the cookie string is cleared,
    /// UserId is kept (useful on re-login). The file is not deleted — the Connect button
    /// reopens the login window.
    /// </summary>
    public void InvalidateSession()
    {
        var file = _auth.Load();
        file.CookieHeader = null;
        _auth.Save(file);
        Logger.Warn("VK web session invalidated — sign in again");
    }

    /// <summary>Disconnect: delete vk_auth.json and the URL cache. The caller clears the table.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        lock (_urlLock) _urlCache.Clear();
    }

    /// <summary>Date of the last successful sync (from vk_auth.json); null — never synced.</summary>
    public DateTime? GetLastSyncedUtc()
    {
        var raw = _auth.Load().LastSyncedAtUtc;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;
    }

    public void SetLastSyncedUtc(DateTime utc)
    {
        var file = _auth.Load();
        file.LastSyncedAtUtc = utc.ToString("o");
        _auth.Save(file);
    }

    // ======================= Synchronization =========================

    /// <summary>
    /// Music sync: fetches the catalog via the al_audio.php web endpoint
    /// (act=load_section) with web session cookies. Returns the number of saved tracks.
    /// Throws <see cref="VkApiException"/> (code 5 — session expired); the VM shows a
    /// clear message.
    /// </summary>
    public async Task<int> SyncAudioAsync(VkTracksRepository repository, CancellationToken ct)
    {
        var rows = await FetchAudioWebAsync(repository, ct);
        return rows.Count;
    }

    /// <summary>
    /// Full catalog fetch (load_section, up to the page guard). With a repository — saves
    /// metadata, records the sync date and downloads artworks; without one (URL refresh) —
    /// only fills the mp3 URL cache. Both paths update _urlCache and _lastUrlRefreshUtc.
    /// </summary>
    private async Task<List<VkTrackRow>> FetchAudioWebAsync(VkTracksRepository? repository, CancellationToken ct)
    {
        var file = _auth.Load();
        var cookieHeader = file.CookieHeader;
        var userId = file.UserId;
        if (string.IsNullOrWhiteSpace(cookieHeader) || string.IsNullOrWhiteSpace(userId))
            throw new VkApiException("not connected", 5);

        var syncedAt = DateTime.UtcNow.ToString("o");
        var all = new List<VkTrackRow>();
        int offset = 0;

        for (int page = 0; page < MaxAudioPages; page++)
        {
            var (status, body) = await PostSectionAsync(cookieHeader, userId, offset, ct);

            if (status != 200)
                throw new VkApiException($"al_audio.php failed with HTTP {status}", 0);

            if (PositionalAudioParser.IsLoginHtml(body))
            {
                InvalidateSession();
                throw new VkApiException("VK returned a login page — session expired", 5);
            }

            var (errorCode, tuples) = PositionalAudioParser.ParseRecentSection(body);
            if (errorCode != 0)
            {
                // Audio auth challenge: the session is reset — signing in again through
                // the window (with vk.ru/audio warm-up) restores access.
                InvalidateSession();
                throw new VkApiException($"al_audio.php returned error {errorCode}", errorCode);
            }

            if (tuples.Count == 0) break;

            if (repository != null)
            {
                var rows = ExtractRecentRows(tuples, syncedAt);
                await repository.UpsertBatchAsync(rows);
                all.AddRange(rows);
            }

            CacheStreamUrls(tuples);

            if (repository != null)
                SyncProgress?.Invoke(this, (all.Count, all.Count + (tuples.Count == MaxSectionSize ? MaxSectionSize : 0)));

            // Pagination: sections return 50 items each; keep fetching while the page is full.
            if (tuples.Count < MaxSectionSize) break;
            offset += tuples.Count;
        }

        _lastUrlRefreshUtc = DateTime.UtcNow;

        if (repository != null)
        {
            SetLastSyncedUtc(DateTime.UtcNow);
            await SyncArtworksAsync(repository, ct);
        }

        return all;
    }

    /// <summary>The "recent" section page returns exactly this many tuples.</summary>
    private const int MaxSectionSize = 50;

    /// <summary>Catalog rows from the recent-section tuples.</summary>
    internal static List<VkTrackRow> ExtractRecentRows(IEnumerable<RecentAudioTuple> tuples, string syncedAt)
    {
        var rows = new List<VkTrackRow>();
        foreach (var t in tuples)
        {
            var vkId = $"{t.OwnerId}_{t.AudioId}";
            var cover = t.ArtworkUrls.Length > 0
                ? t.ArtworkUrls.Split(',')[0].Trim()
                : string.Empty;

            rows.Add(new VkTrackRow
            {
                VkId = vkId,
                Title = t.Title,
                Artist = t.Artist,
                DurationMs = t.DurationSec * 1000,
                ArtworkUrl = cover,
                UrlHash = t.UrlHash,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    /// <summary>
    /// Next offset, or null — the page was the last. An explicit nextOffset from the payload
    /// (when VK sends it and it moves forward) takes priority over the total-based calculation;
    /// the next offset is computed by the same pure function <see cref="NextAudioOffset"/> (unit-tested).
    /// </summary>
    internal static int? NextWebOffset(int currentOffset, AlAudioPayload payload)
    {
        if (payload.NextOffset is int explicitNext && explicitNext > currentOffset)
            return explicitNext;

        return NextAudioOffset(currentOffset, payload.Tracks.Count, payload.ReportedTotal, PageSize);
    }

    /// <summary>
    /// Next load_section pagination offset, or null — the page was the last.
    /// Pure function (unit-tested):
    ///   • empty page — end;
    ///   • short page (less than the requested size) — end;
    ///   • accumulated item count reached the reported total — end.
    /// </summary>
    internal static int? NextAudioOffset(int currentOffset, int fetchedItems, int reportedTotal, int pageSize)
    {
        if (fetchedItems <= 0) return null;
        if (reportedTotal > 0 && currentOffset + fetchedItems >= reportedTotal) return null;
        if (fetchedItems < pageSize) return null;
        return currentOffset + fetchedItems;
    }

    /// <summary>
    /// Batch download of all VK track artworks to artworks_cache/vk_{vk_id}.jpg (see
    /// <see cref="VkArtworkCache"/>): up to <see cref="ArtworkDownloadParallelism"/> parallel
    /// downloads, skipping already downloaded files and tracks without artwork_url. Per-file
    /// errors are logged and skipped. DB results are written sequentially (one connection).
    /// </summary>
    private async Task SyncArtworksAsync(VkTracksRepository repository, CancellationToken ct)
    {
        try
        {
            var rows = (await repository.GetAllAsync())
                .Where(r => !string.IsNullOrEmpty(r.ArtworkUrl))
                .ToList();
            if (rows.Count == 0) return;

            var gate = new SemaphoreSlim(ArtworkDownloadParallelism);
            var results = await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var path = await _artworks.EnsureDownloadedAsync(ArtworkIdPrefix + row.VkId, row.ArtworkUrl, ct);
                    return (row.VkId, Path: path, Existing: row.ArtworkLocalPath);
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var (vkId, path, existing) in results)
            {
                if (path != null && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    await repository.SetArtworkLocalPathAsync(vkId, path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Artworks are auxiliary to the sync: a network failure must not affect the metadata.
            Logger.Error(ex, "VK artwork batch download failed");
        }
    }

    /// <summary>Prefix of VK artwork files in the shared artworks_cache directory.</summary>
    internal const string ArtworkIdPrefix = "vk_";

    // ============================ Streaming ==========================

    /// <summary>
    /// Direct mp3 URL of a track for streaming. URLs from load_section are temporary and
    /// not saved to the DB: they are kept in session memory (TTL); if a URL expired or the
    /// session is new — the catalog is re-fetched once and tried again; the last resort is
    /// a targeted reload_audio for the specific track (the type=recent catalog is incomplete).
    /// null — the URL could not be obtained (no session, track unavailable).
    /// </summary>
    public async Task<string?> GetPlayableStreamAsync(VkTrackRow? track, CancellationToken ct)
    {
        if (track == null || string.IsNullOrEmpty(track.VkId)) return null;

        if (TryGetFreshUrl(track.VkId, out var url)) return url;

        try
        {
            await RefreshStreamUrlsAsync(ct);
        }
        catch (VkApiException ex)
        {
            Logger.Error($"VK stream URL refresh failed (error code {ex.ErrorCode})");
            // Full fetch failed (network/challenge) — fall back to targeted reload_audio.
            return await ResolveReloadAudioAsync(track, ct);
        }

        if (TryGetFreshUrl(track.VkId, out url)) return url;

        // The track is missing from fresh catalog sections (imported long ago, beyond the
        // first pages) or its hash does not decode — targeted reload_audio by its id.
        return await ResolveReloadAudioAsync(track, ct);
    }

    /// <summary>reload_audio failure tracking: retry no earlier than the TTL — an unavailable
    /// track must not hammer VK on every click.</summary>
    private static readonly TimeSpan ReloadFailureTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _reloadFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the URL of a single track: POST al_audio.php act=reload_audio
    /// ids={owner}_{id} — the call the web player uses to fetch the URL on click.
    /// The response is the same positional format; StreamUrl is extracted by the lenient parser.
    /// null — VK gave no URL (unavailable/deleted) or a recent attempt already failed.
    /// </summary>
    private async Task<string?> ResolveReloadAudioAsync(VkTrackRow track, CancellationToken ct)
    {
        lock (_urlLock)
        {
            if (_reloadFailures.TryGetValue(track.VkId, out var failed)
                && DateTime.UtcNow - failed < ReloadFailureTtl)
            {
                return null;
            }
        }

        try
        {
            var url = await FetchReloadAudioUrlAsync(track, ct);
            if (url != null)
            {
                lock (_urlLock) _urlCache[track.VkId] = (url, DateTime.UtcNow);
                return url;
            }

            lock (_urlLock) _reloadFailures[track.VkId] = DateTime.UtcNow;
            return null;
        }
        catch (VkApiException ex)
        {
            // Session reset/challenge — the resolver must not break playback.
            Logger.Error($"VK reload_audio failed for {track.VkId} (error code {ex.ErrorCode})");
            lock (_urlLock) _reloadFailures[track.VkId] = DateTime.UtcNow;
            return null;
        }
    }

    /// <summary>reload_audio network call + parsing. Internal so the testable part is separate.</summary>
    private async Task<string?> FetchReloadAudioUrlAsync(VkTrackRow track, CancellationToken ct)
    {
        var file = _auth.Load();
        var cookieHeader = file.CookieHeader;
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;

        var body = await PostReloadAudioAsync(cookieHeader, $"{track.VkId}", ct);
        if (PositionalAudioParser.IsLoginHtml(body))
        {
            InvalidateSession();
            throw new VkApiException("VK returned a login page — session expired", 5);
        }

        return ExtractReloadAudioUrl(PositionalAudioParser.ParsePayload(body), track, file.UserId ?? string.Empty);
    }

    /// <summary>
    /// URL from the reload_audio response: exact vk_id match; if exactly one track parsed —
    /// take it (the request was for one id; the id/owner_id layout may have shifted).
    /// An audio_api_unavailable.mp3?extra=... URL is decoded the same way as the catalog
    /// section hashes; unplayable responses yield null.
    /// </summary>
    internal static string? ExtractReloadAudioUrl(AlAudioPayload payload, VkTrackRow track, string userId)
    {
        if (payload.ErrorCode != 0 || !payload.Parsed) return null;

        var parsed = payload.Tracks;
        var match = parsed.FirstOrDefault(t => t.VkId == track.VkId)
                    ?? (parsed.Count == 1
                        && string.Equals(parsed[0].Title, track.Title, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(parsed[0].Artist, track.Artist, StringComparison.OrdinalIgnoreCase)
                        ? parsed[0] : null);

        var url = match?.StreamUrl;
        if (string.IsNullOrEmpty(url)) return null;

        if (url.Contains("audio_api_unavailable.mp3", StringComparison.Ordinal))
        {
            // The decoder needs viewer_id as a number; without it the URL cannot be built.
            if (!long.TryParse(userId, out var vkUserId) || vkUserId == 0) return null;
            url = VkAudioUrlDecoder.Decode(url, vkUserId);
        }

        return string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.Ordinal) ? null : url;
    }

    /// <summary>Refreshes the URL cache with a full catalog fetch (no more often than the cooldown).</summary>
    private async Task RefreshStreamUrlsAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastUrlRefreshUtc < UrlRefreshCooldown) return;
            await FetchAudioWebAsync(repository: null, ct);
            _lastUrlRefreshUtc = DateTime.UtcNow;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool TryGetFreshUrl(string vkId, out string url)
    {
        lock (_urlLock)
        {
            if (_urlCache.TryGetValue(vkId, out var entry)
                && DateTime.UtcNow - entry.FetchedUtc < StreamUrlTtl)
            {
                url = entry.Url;
                return true;
            }
        }
        url = string.Empty;
        return false;
    }

    /// <summary>Caches the temporary URLs of a catalog page (unplayable ones are skipped).</summary>
    private void CacheStreamUrls(IEnumerable<RecentAudioTuple> tuples)
    {
        var now = DateTime.UtcNow;
        var userId = _auth.Load().UserId;
        if (string.IsNullOrEmpty(userId) || !long.TryParse(userId, out var vkUserId))
            return;

        lock (_urlLock)
        {
            foreach (var tuple in tuples)
            {
                if (string.IsNullOrEmpty(tuple.UrlHash)) continue;
                
                // The reload_audio API is not implemented — build unavailable.mp3 and decode
                var unavailableUrl = $"https://vk.ru/mp3/audio_api_unavailable.mp3?extra={tuple.UrlHash}";
                var streamUrl = VkAudioUrlDecoder.Decode(unavailableUrl, vkUserId);
                
                if (string.IsNullOrEmpty(streamUrl) || !streamUrl.StartsWith("http", StringComparison.Ordinal))
                    continue;
                
                var vkId = $"{tuple.OwnerId}_{tuple.AudioId}";
                _urlCache[vkId] = (streamUrl, now);
            }
        }
    }

    // ====================== Network primitives =======================

    /// <summary>
    /// POST al_audio.php: vk.ru first, legacy vk.com on an unclear response.
    /// The body is returned as is: parsing (including login-page detection) is up to the caller.
    /// Cookies travel in the header and are never logged.
    /// </summary>
    private static async Task<(int Status, string Body)> PostSectionAsync(
        string cookieHeader, string userId, int offset, CancellationToken ct)
    {
        var (ruStatus, ruBody) = await PostAlAudioOnceAsync(WebAudioUrlRu, cookieHeader, userId, offset, ct);
        if (ruStatus == 200 && ruBody.Contains("<!json>", StringComparison.Ordinal))
            return (ruStatus, ruBody);

        Logger.Info($"VK al_audio.php on vk.ru answered HTTP {ruStatus} without payload — trying vk.com");
        return await PostAlAudioOnceAsync(WebAudioUrlCom, cookieHeader, userId, offset, ct);
    }

    /// <summary>
    /// A single load_section POST. Form body matches the VK web player:
    /// act=load_section&amp;al=1&amp;claim=0&amp;offset=N&amp;owner_id=UID&amp;type=recent&amp;utf8=1.
    /// </summary>
    private static async Task<(int Status, string Body)> PostAlAudioOnceAsync(
        string url, string cookieHeader, string userId, int offset, CancellationToken ct)
    {
        return await VkHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["act"] = "load_section",
                    ["al"] = "1",
                    ["claim"] = "0",
                    ["offset"] = offset.ToString(),
                    ["owner_id"] = userId,
                    ["type"] = "recent",
                    ["utf8"] = "1"
                })
            };
            // The session cookie string is the request's only auth (never logged).
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "com.vkontakte.android");
            request.Headers.TryAddWithoutValidation("Referer", WebAudioReferer);
            // Extra headers mimicking the VK mobile app
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            return request;
        }, ct);
    }

    /// <summary>
    /// POST al_audio.php act=reload_audio — targeted URL fetch for one track
    /// (the same call the web player makes on click). Form: act=reload_audio&amp;al=1&amp;ids=…
    /// </summary>
    private static async Task<string> PostReloadAudioAsync(
        string cookieHeader, string ids, CancellationToken ct)
    {
        foreach (var url in new[] { WebAudioUrlRu, WebAudioUrlCom })
        {
            var (status, body) = await VkHttp.SendWithFailoverAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["act"] = "reload_audio",
                        ["al"] = "1",
                        ["ids"] = ids
                    })
                };
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                request.Headers.TryAddWithoutValidation("X-Requested-With", "com.vkontakte.android");
                request.Headers.TryAddWithoutValidation("Referer", WebAudioReferer);
                request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                return request;
            }, ct);

            if (status == 200 && body.Contains("<!json>", StringComparison.Ordinal))
                return body;
            Logger.Info($"VK al_audio.php reload_audio on {url} answered HTTP {status} without payload");
        }

        return string.Empty;
    }

    /// <summary>The error code means audio access is denied (message — VkAudioPermissionDenied).</summary>
    public static bool IsAudioPermissionError(int errorCode) => errorCode is 15 or 26 or 201;

    // ==================== Response parsing (tested) =================

    /// <summary>
    /// Parsed tracks → DB rows. Hidden/deleted entries (no url — IsPlayable=false) are not
    /// written to the catalog. duration in the payload is in seconds; vk_id = "{owner_id}_{id}".
    /// </summary>
    internal static List<VkTrackRow> ExtractRows(IEnumerable<ParsedWebAudio> tracks, string syncedAt)
    {
        var rows = new List<VkTrackRow>();
        foreach (var track in tracks)
        {
            if (!track.IsPlayable || track.AudioId == 0 || track.OwnerId == 0) continue;

            rows.Add(new VkTrackRow
            {
                VkId = track.VkId,
                Title = track.Title ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                DurationMs = Math.Max(0, track.DurationSec) * 1000,
                ArtworkUrl = track.ArtworkUrl ?? string.Empty,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    /// <summary>vk_id = "{owner_id}_{id}" (a track id is unique only together with its owner).</summary>
    internal static string BuildVkId(long ownerId, long id) => $"{ownerId}_{id}";

    // ===================== Web session userId ========================

    private static readonly System.Text.RegularExpressions.Regex[] UserIdPatterns =
    [
        // Priority order: embedded page configs contain the viewer id;
        // profile links are the last resort (they may point to another user).
        new("\"uid\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("\"viewer_id\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("viewer_id=(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("\"user_id\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("href=\"/id(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled)
    ];

    /// <summary>
    /// User id from a VK HTML page (feed/boot data/profile links), or null.
    /// Pure function — unit-tested. The login window calls it for HTML captured from
    /// WebView2 (ExecuteScriptAsync) and downloaded via HttpClient with session cookies.
    /// </summary>
    internal static string? ExtractUserIdFromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        foreach (var pattern in UserIdPatterns)
        {
            var match = pattern.Match(html);
            if (match.Success)
            {
                var value = match.Groups[1].Value;
                if (value.Length > 0 && value != "0") return value;
            }
        }
        return null;
    }
}
