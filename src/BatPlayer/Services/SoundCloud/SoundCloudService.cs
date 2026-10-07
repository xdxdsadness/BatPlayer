using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Database;
using BatPlayer.Services;

using BatPlayer.Localization;

namespace BatPlayer.Services.SoundCloud;

/// <summary>SoundCloud API error after all retries (401/403 with a fresh client_id, 5xx, etc.).</summary>
public sealed class SoundCloudApiException : Exception
{
    public int StatusCode { get; }

    public SoundCloudApiException(string message, int statusCode) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// Client for the unofficial SoundCloud web API (api-v2.soundcloud.com).
/// Auth via web-session cookies (sc_auth.json) obtained through the login window (WebView2).
/// Audio download is not implemented here: metadata, streaming (direct mp3 link) and matching only.
/// Network via the shared static SoundCloudHttp layer: direct with proxy fallback (see its doc).
/// Cookies/client_id are never logged — only response codes and token-free URLs reach logs/exceptions.
/// </summary>
public sealed class SoundCloudService
{
    public const string AuthFileName = "sc_auth.json";
    private const string ApiBase = "https://api-v2.soundcloud.com";
    // AAC transcoding mime is "audio/mp4; codecs=\"mp4a.40.2\"": compare by prefix.
    private const string AacMimeType = "audio/mp4";
    private const int PageSize = 200;
    // Paging loop guard against a broken next_href (~50 pages × 200 = 10k likes).
    private const int MaxLikePages = 50;

    // Without a UA SoundCloud serves a simplified page/403 for html requests.
    // internal: set by the SoundCloudHttp factory on the shared HttpClients.
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions JsonOpts = new();

    // Artwork batch download parallelism (see SyncArtworksAsync).
    private const int ArtworkDownloadParallelism = 4;

    private readonly SoundCloudAuthStore _auth;
    private readonly SoundCloudClientIdProvider _clientIds;
    private readonly SoundCloudArtworkCache _artworks = new();

    /// <summary>On-demand artwork downloads: deduplicates parallel requests for one track
    /// (visible-card loader and batch sync); completed tasks are not retained.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _artworkDownloads = new();

    /// <summary>Artwork cache file path for a track (cards bind to exactly this).</summary>
    public string GetArtworkCachePath(string scId) => _artworks.GetCacheFilePath(scId);

    /// <summary>On-demand artwork cache path (visible card): download if the file is missing.
    /// Parallel requests for one scId share a single download. Path or null (error/no URL).</summary>
    public Task<string?> EnsureArtworkPathAsync(string scId, string? artworkUrl, CancellationToken ct)
        => _artworkDownloads.GetOrAdd(scId, _ => Task.Run(() => DownloadArtworkAsyncCore(scId, artworkUrl)));

    private async Task<string?> DownloadArtworkAsyncCore(string scId, string? artworkUrl)
    {
        try
        {
            // No external ct: the task is shared by several requesters; cancelling one
            // caller must not spoil the result for the others.
            return await _artworks.EnsureDownloadedAsync(scId, artworkUrl, CancellationToken.None);
        }
        finally
        {
            // Completed (incl. failed) tasks are not kept: the next request either finds
            // the file in cache or honestly retries the download.
            _artworkDownloads.TryRemove(scId, out _);
        }
    }

    /// <summary>Likes sync progress: (done, total estimate).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    /// <summary>Official connection (pairing code): when connected, track streams resolve
    /// ONLY through it — no need to mix with the unofficial api-v2.</summary>
    private readonly SoundCloudOfficialApi? _officialApi;

    /// <summary>Whether the official SoundCloud API is connected (OAuth token of the pairing connection).</summary>
    public bool OfficialApiConnected => _officialApi?.IsConnected == true;


    // ===== Web-session fuse =====
    // A 401 from api-v2 after a client_id refresh means the oauth_token cookie expired.
    // Regular tracks are unaffected (media resolve works anonymously), but MONETIZE
    // (AD_SUPPORTED) tracks answer 404 from media endpoints without a live session —
    // without this flag they look like "DRM/unavailable".
    private volatile bool _webSessionExpired;

    /// <summary>Web session (cookies) expired: the last /me (or other authorized api-v2
    /// request) answered 401. Cleared by a successful /me and saving new cookies.</summary>
    public bool IsWebSessionExpired => _webSessionExpired;

    /// <summary>Session cookies re-saved (login/account reconnect): listeners reset
    /// session-dependent caches (the resolve-failure blacklist).</summary>
    public event EventHandler? SessionChanged;

    private void MarkWebSessionExpired()
    {
        if (_webSessionExpired) return;
        _webSessionExpired = true;
        Logger.Warn("SoundCloud web session expired (api-v2 401) — reconnect the account in Settings; " +
                    "MONETIZE (ad-supported) tracks need an authenticated session");
    }

    /// <summary>
    /// Is the web session alive: GET /me. true — alive; false — expired (401 after a
    /// client_id refresh); null — undetermined (network down / other code) — no diagnosis,
    /// so a network failure doesn't read as "please reconnect".
    /// </summary>
    public async Task<bool?> VerifyWebSessionAsync(CancellationToken ct)
    {
        if (!_auth.Exists) return false;
        try
        {
            var (status, _) = await SendApiAsync($"{ApiBase}/me", forceRefreshClientId: false, ct);
            if (status == 200)
            {
                _webSessionExpired = false;
                return true;
            }
            if (status == 401)
            {
                MarkWebSessionExpired();
                return false;
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // network failure — no "session expired" diagnosis
        }
    }

    public SoundCloudService(string authFilePath, SettingsService? settings = null,
        SoundCloudOfficialApi? officialApi = null)
    {
        _officialApi = officialApi;
        _auth = new SoundCloudAuthStore(authFilePath);

        // Shared network layer (direct/proxy/anti-blocking) for us and SoundCloudClientIdProvider;
        // on a settings change, re-read SoundCloudProxy and rebuild the transport choice.
        SoundCloudHttp.DpiBypassEnabled = settings?.Current.DpiBypassEnabled ?? true;
        SoundCloudHttp.ApplyUserSetting(settings?.Current.SoundCloudProxy);
        // events can't be combined with ?. (CS0070: reading an event outside its declaring type).
        if (settings != null)
            settings.SettingsChanged += (_, _) =>
            {
                SoundCloudHttp.DpiBypassEnabled = settings.Current.DpiBypassEnabled;
                SoundCloudHttp.ApplyUserSetting(settings.Current.SoundCloudProxy);
            };

        _clientIds = new SoundCloudClientIdProvider(_auth);
    }

    /// <summary>Whether the web-session file exists (validity not checked).</summary>
    public bool HasAuthFile => _auth.Exists;

    // ============================ Session ============================

    /// <summary>
    /// Session check: GET /me. 200 → user; otherwise null (not connected / stale cookies).
    /// </summary>
    public async Task<ScMeResponse?> GetMeAsync(CancellationToken ct)
    {
        try
        {
            var (status, json) = await SendApiAsync($"{ApiBase}/me", forceRefreshClientId: false, ct);
            if (status != 200) return null;
            return JsonSerializer.Deserialize<ScMeResponse>(json, JsonOpts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud /me check failed");
            return null;
        }
    }

    // ========================= Sync ========================

    /// <summary>
    /// Likes sync: GET /users/{userId}/likes (limit=200), paginating via next_href — a full
    /// URL with a like-time cursor (NOT an offset number), followed until absent.
    /// collection[].playlist items are skipped (Track == null in the DTO).
    /// UserId comes from the sc_auth.json cache or is resolved via /me (see GetUserIdAsync).
    /// Returns the number of saved tracks. Throws SoundCloudApiException — the VM shows the error.
    /// </summary>
    public async Task<int> SyncLikesAsync(SoundCloudLikesRepository repository, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var syncedAt = DateTime.UtcNow.ToString("o");
        int done = 0;

        // First request by user id; then follow next_href from the response.
        string? nextUrl = $"{ApiBase}/users/{Uri.EscapeDataString(userId)}/likes?limit={PageSize}";
        for (int page = 0; page < MaxLikePages && nextUrl != null; page++)
        {
            var (status, json) = await SendApiAsync(nextUrl, forceRefreshClientId: false, ct);
            if (status != 200)
                throw new SoundCloudApiException($"likes request failed with HTTP {status}", status);

            var response = ParseLikesJson(json)
                ?? throw new SoundCloudApiException("likes response parse failed", status);

            var rows = ExtractLikeRows(response, syncedAt);
            if (rows.Count > 0)
                await repository.UpsertBatchAsync(rows);

            done += rows.Count;
            // total is unknown until paging ends: estimate done + one page if continuing.
            SyncProgress?.Invoke(this, (done, done + (response.NextHref != null ? PageSize : 0)));

            // next_href already carries client_id; AppendClientId is idempotent.
            nextUrl = string.IsNullOrEmpty(response.NextHref) ? null : response.NextHref;
            if (response.Collection.Count == 0) break; // guard against a broken empty response
        }

        if (nextUrl != null)
            Logger.Warn($"SoundCloud likes pagination stopped at page cap ({MaxLikePages} pages)");

        SetLastSyncedUtc(DateTime.UtcNow);

        // Artwork is a separate batch AFTER the main like upsert: likes are already saved,
        // so an artwork failure doesn't kill the finished sync. Paths go to artwork_local_path;
        // cards bind the local file (i1.sndcdn.com is unreachable directly).
        await SyncArtworksAsync(repository, ct);

        return done;
    }

    /// <summary>
    /// Batch-downloads artwork for all likes into artworks_cache/{scId}.jpg (see SoundCloudArtworkCache):
    /// up to <see cref="ArtworkDownloadParallelism"/> parallel downloads, skipping already-downloaded
    /// items and likes without artwork_url. Per-file errors: logged and skipped. Results go to
    /// the DB (artwork_local_path) sequentially — the repository has a single SqliteConnection.
    /// Also backfills: likes from older syncs without a local artwork are downloaded.
    /// </summary>
    private async Task SyncArtworksAsync(SoundCloudLikesRepository repository, CancellationToken ct)
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
                    // Via the shared dedup: a currently visible card may have already started
                    // downloading the same artwork — parallel writes to one .part are not allowed.
                    var path = await EnsureArtworkPathAsync(row.ScId, row.ArtworkUrl, ct);
                    return (row.ScId, Path: path, Existing: row.ArtworkLocalPath);
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var (scId, path, existing) in results)
            {
                if (path != null && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    await repository.SetArtworkLocalPathAsync(scId, path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Artwork is auxiliary to the sync: a network failure must not hurt the likes.
            Logger.Error(ex, "SoundCloud artwork batch download failed");
        }
    }

    /// <summary>
    /// UserId of the connected user: from the sc_auth.json cache (UserId field); if empty —
    /// GET /me, and the id is saved to the auth file for future syncs.
    /// </summary>
    private async Task<string> GetUserIdAsync(CancellationToken ct)
    {
        var file = _auth.Load();
        if (!string.IsNullOrEmpty(file.UserId)) return file.UserId;

        var me = await GetMeAsync(ct)
            ?? throw new SoundCloudApiException("cannot resolve user id: /me request failed", 0);
        if (me.Id == 0)
            throw new SoundCloudApiException("/me returned no user id", 0);

        file.UserId = me.Id.ToString();
        _auth.Save(file);
        return file.UserId;
    }

    /// <summary>Last successful sync date (from sc_auth.json), null — never synced.</summary>
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

    // ============================ Stream ==============================

    /// <summary>
    /// Full track info by id (needed for cards loaded from the DB without transcodings).
    /// </summary>
    public async Task<ScTrack?> GetTrackAsync(string scId, CancellationToken ct)
    {
        var (status, json) = await SendApiAsync($"{ApiBase}/tracks/{Uri.EscapeDataString(scId)}",
            forceRefreshClientId: false, ct);
        return status == 200 ? ParseTrackJson(json) : null;
    }

    /// <summary>
    /// SoundCloud related tracks (GET /tracks/{id}/related) — the track's "scene" per the
    /// SoundCloud graph, pool E of My Wave. null — network/error/non-200 (client_id refresh
    /// on 401/403 happens inside SendApiAsync). A network failure doesn't break wave generation.
    /// </summary>
    public async Task<List<ScTrack>?> GetRelatedTracksAsync(string scId, int limit, CancellationToken ct)
    {
        try
        {
            var (status, json) = await SendApiAsync(
                $"{ApiBase}/tracks/{Uri.EscapeDataString(scId)}/related?limit={Math.Clamp(limit, 1, 50)}",
                forceRefreshClientId: false, ct);
            return status == 200
                ? JsonSerializer.Deserialize<ScRelatedResponse>(json, JsonOpts)?.Collection
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud related-tracks failed ({scId})");
            return null;
        }
    }

    /// <summary>
    /// Resolves a direct mp3 link for streaming: progressive transcoding →
    /// GET {transcoding.url}?client_id=... → JSON {"url": "..."}.
    /// null — no progressive or the CDN answered non-200: use DownloadHlsMp3Async.
    /// </summary>
    public async Task<string?> GetPlayableStreamAsync(ScTrack track, CancellationToken ct)
    {
        var transcodingUrl = PickProgressiveUrl(track);
        if (transcodingUrl == null) return null;

        return await ResolveTranscodingAsync(transcodingUrl, ct);
    }

    /// <summary>GET {transcoding.url}?client_id → JSON {"url": "..."}. 401/403/404 — one
    /// client_id refresh and retry (a stale id answers both 401 and 404 at media endpoints);
    /// non-200 → null (logged).</summary>
    private async Task<string?> ResolveTranscodingAsync(string transcodingUrl, CancellationToken ct)
    {
        var clientId = await _clientIds.GetClientIdAsync(forceRefresh: false, ct);
        var url = AppendClientId(transcodingUrl, clientId);
        var (status, json) = await SendWithAuthAsync(url, ct);
        if (status == 401 || status == 403 || status == 404)
        {
            // client_id may have gone stale: refresh once and retry — but only if the id
            // actually changed; the same id always yields the same answer.
            var fresh = await _clientIds.GetClientIdAsync(forceRefresh: true, ct);
            if (!string.IsNullOrEmpty(fresh) && fresh != clientId)
            {
                url = AppendClientId(transcodingUrl, fresh);
                (status, json) = await SendWithAuthAsync(url, ct);
            }
        }
        if (status != 200)
        {
            Logger.Error($"SoundCloud stream resolve failed with HTTP {status} for {MediaPath(transcodingUrl)}");
            return null;
        }

        try
        {
            var resolved = JsonSerializer.Deserialize<ScStreamResolve>(json, JsonOpts);
            return string.IsNullOrEmpty(resolved?.Url) ? null : resolved!.Url;
        }
        catch (JsonException ex)
        {
            Logger.Error(ex, "SoundCloud stream resolve parse failed");
            return null;
        }
    }

    /// <summary>Official SoundCloud API: GET /tracks/{urn}/streams (OAuth token of the
    /// pairing connection, refreshed on 401 — in SoundCloudOfficialApi) → official AAC
    /// HLS playlist — works for EVERYTHING the account can access (incl. Go+) and does not
    /// depend on the unofficial api-v2 (which periodically 404s media endpoints). Assembly
    /// and duration patching use the same mechanics as the unofficial AAC path.
    /// expectedDurationMs — track duration from metadata: if the official stream is much
    /// shorter, it is a 30-second snippet (no full-version rights) — never play a snippet
    /// under full metadata.
    /// null — no official connection / no streams / network failure.</summary>
    public async Task<byte[]?> DownloadOfficialAacAsync(string scId, long expectedDurationMs, CancellationToken ct)
    {
        if (_officialApi == null || string.IsNullOrEmpty(scId)) return null;
        // The API is blocked for the client entirely: a request per track only slows resolution
        // (the same 403 is guaranteed) — go straight to the unofficial cascade. Once every
        // half hour a probe request still goes out (ShouldSkipStreamsProbe), in case of unblocking.
        if (_officialApi.ShouldSkipStreamsProbe) return null;

        var urls = await _officialApi.GetStreamUrlsAsync(scId, ct);
        if (urls == null) return null;

        // 1) Official AAC (hls_aac_160 — best option).
        if (urls.HlsAac160 != null)
        {
            var aac = await DownloadOfficialAacPlaylistAsync(urls.HlsAac160, scId, expectedDurationMs, ct);
            if (aac != null)
            {
                Logger.Info($"SoundCloud official AAC assembled ({aac.Length / 1024} KB)");
                return aac;
            }
            Logger.Warn("SoundCloud official AAC failed — falling back to official MP3");
        }

        // 2) MP3 fallback: official HLS mp3 playlist, segments are self-contained mp3.
        if (urls.HlsMp3128 == null) return null;
        var (mp3Status, mp3PlaylistBody) = await OfficialGetAsync(urls.HlsMp3128, ct);
        if (mp3Status != 200) return null;

        var mp3 = ParseHlsAacPlaylist(mp3PlaylistBody, new Uri(urls.HlsMp3128));
        if (mp3.Segments.Count == 0)
        {
            Logger.Warn("SoundCloud official MP3 HLS playlist has no segments");
            return null;
        }
        if (IsSnippet(mp3.TotalSeconds * 1000, expectedDurationMs, scId)) return null;

        var mp3Parts = new byte[mp3.Segments.Count][];
        var mp3Gate = new SemaphoreSlim(HlsSegmentParallelism);
        var mp3Errors = 0;
        await Task.WhenAll(mp3.Segments.Select(async (segmentUrl, index) =>
        {
            await mp3Gate.WaitAsync(ct);
            try
            {
                var bytes = await DownloadSegmentBytesAsync(segmentUrl, ct);
                if (bytes == null) Interlocked.Increment(ref mp3Errors);
                else mp3Parts[index] = bytes;
            }
            finally
            {
                mp3Gate.Release();
            }
        }));
        if (mp3Errors > 0)
        {
            Logger.Warn($"SoundCloud official MP3 HLS segments failed: {mp3Errors}/{mp3.Segments.Count}");
            return null;
        }

        using var mp3Output = new MemoryStream(mp3Parts.Sum(p => p?.Length ?? 0));
        foreach (var part in mp3Parts) mp3Output.Write(part, 0, part.Length);
        Logger.Info($"SoundCloud official MP3 HLS assembled: {mp3.Segments.Count} segments");
        return mp3Output.ToArray();
    }

    /// <summary>An official stream much shorter than the metadata is a preview snippet
    /// (no full-version rights). Only full versions are played: a snippet under the card's
    /// full metadata reads as "wrong sound".</summary>
    private static bool IsSnippet(double actualMs, long expectedDurationMs, string scId)
    {
        if (actualMs <= 0 || expectedDurationMs <= 0) return false;
        if (actualMs >= expectedDurationMs * 0.75) return false;
        Logger.Warn($"SoundCloud official stream is a snippet " +
                    $"({actualMs / 1000:0}s of {expectedDurationMs / 1000:0}s, scId={scId}) — no full playback rights");
        return true;
    }

    private async Task<(int status, string body)> OfficialGetAsync(string url, CancellationToken ct)
    {
        // Token per request: it may have been refreshed here after a 401 on /streams.
        var token = await _officialApi!.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return (401, "");

        var (status, stream, owner) = await SoundCloudHttp.SendStreamAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", token);
            return req;
        }, ct);
        using (owner)
        {
            if (status != 200 || stream == null) return (status, "");
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return (status, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    /// <summary>Official AAC playlist: fetch (2 nesting levels) + init segment +
    /// parallel segments + fMP4 duration patch. Snippet (see IsSnippet) → null.
    /// null — failure at any step.</summary>
    private async Task<byte[]?> DownloadOfficialAacPlaylistAsync(
        string aacUrl, string scId, long expectedDurationMs, CancellationToken ct)
    {
        string playlistBody = aacUrl;
        string? mediaUrl = null;
        for (var depth = 0; depth < 2; depth++)
        {
            var (status, body) = await OfficialGetAsync(playlistBody, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud official AAC HLS playlist HTTP {status}");
                return null;
            }
            if (!body.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                playlistBody = body;
                break;
            }
            var subs = ParseHlsPlaylist(body, new Uri(aacUrl));
            if (subs.Count == 0) return null;
            mediaUrl = subs[0];
            playlistBody = mediaUrl;
        }
        if (mediaUrl != null)
        {
            var (status, mediaBody) = await OfficialGetAsync(mediaUrl, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud official AAC media playlist HTTP {status}");
                return null;
            }
            playlistBody = mediaBody;
        }

        var playlist = ParseHlsAacPlaylist(playlistBody, new Uri(aacUrl));
        if (playlist.Segments.Count == 0)
        {
            Logger.Warn("SoundCloud official AAC HLS playlist has no segments");
            return null;
        }
        if (IsSnippet(playlist.TotalSeconds * 1000, expectedDurationMs, scId)) return null;

        byte[]? initBytes = null;
        if (playlist.InitUrl != null)
        {
            initBytes = await DownloadSegmentBytesAsync(playlist.InitUrl, ct);
            if (initBytes == null)
            {
                Logger.Warn("SoundCloud official AAC init segment download failed");
                return null;
            }
        }

        var parts = new byte[playlist.Segments.Count][];
        var gate = new SemaphoreSlim(HlsSegmentParallelism);
        var errors = 0;
        await Task.WhenAll(playlist.Segments.Select(async (segmentUrl, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bytes = await DownloadSegmentBytesAsync(segmentUrl, ct);
                if (bytes == null) Interlocked.Increment(ref errors);
                else parts[index] = bytes;
            }
            finally
            {
                gate.Release();
            }
        }));
        if (errors > 0)
        {
            Logger.Warn($"SoundCloud official AAC HLS segments failed: {errors}/{playlist.Segments.Count}");
            return null;
        }

        var durationMs = playlist.TotalSeconds > 0
            ? (long)(playlist.TotalSeconds * 1000)
            : 0;

        using var output = new MemoryStream((initBytes?.Length ?? 0) + playlist.Segments.Sum(p => p?.Length ?? 0));
        if (initBytes != null) output.Write(initBytes, 0, initBytes.Length);
        foreach (var part in parts) output.Write(part, 0, part.Length);

        var patched = Fmp4DurationPatcher.Patch(output.ToArray(), durationMs);
        return patched;
    }

    /// <summary>
    /// HLS fallback: SC moved delivery to HLS — some tracks lack progressive transcoding or
    /// its CDN resolve returns HTTP 404. Take the hls variant with mp3 segments (mime
    /// audio/mpeg), fetch the playlist and stitch the segments into one mp3 (segments are
    /// self-contained, so stitching is valid). null — no HLS / download failed.
    /// </summary>
    public async Task<byte[]?> DownloadHlsMp3Async(ScTrack track, CancellationToken ct)
    {
        var transcodingUrl = PickHlsMp3Url(track);
        if (transcodingUrl == null) return null;

        var playlistUrl = await ResolveTranscodingAsync(transcodingUrl, ct);
        if (playlistUrl == null) return null;

        // The resolved playlist URL is already CloudFront-signed (expires/Policy/Signature/
        // Key-Pair-Id). Appending client_id breaks the signature (verified: HTTP 403), so the
        // playlist must be fetched exactly as resolved.
        var segments = await FetchHlsSegmentUrlsAsync(playlistUrl, ct);
        if (segments.Count == 0)
        {
            Logger.Warn("SoundCloud HLS playlist has no segments");
            return null;
        }

        // Segments download with bounded parallelism and are assembled strictly in order:
        // one bad segment breaks the stitch — return null (the track stays unplayable).
        var parts = new byte[segments.Count][];
        var gate = new SemaphoreSlim(HlsSegmentParallelism);
        var errors = 0;
        await Task.WhenAll(segments.Select(async (segmentUrl, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bytes = await DownloadSegmentBytesAsync(segmentUrl, ct);
                if (bytes == null) Interlocked.Increment(ref errors);
                else parts[index] = bytes;
            }
            finally
            {
                gate.Release();
            }
        }));
        if (errors > 0)
        {
            Logger.Warn($"SoundCloud HLS segments failed: {errors}/{segments.Count}");
            return null;
        }

        using var output = new MemoryStream(segments.Sum(p => p?.Length ?? 0));
        foreach (var part in parts) output.Write(part, 0, part.Length);
        Logger.Info($"SoundCloud HLS assembled: {segments.Count} segments, {output.Length / 1024} KB");
        return output.ToArray();
    }

    /// <summary>HLS segment download parallelism.</summary>
    private const int HlsSegmentParallelism = 4;

    /// <summary>
    /// Segment URLs of the playlist: the media playlist lists them directly; a master
    /// (#EXT-X-STREAM-INF) has one nesting level (take the first variant).
    /// </summary>
    private async Task<List<string>> FetchHlsSegmentUrlsAsync(string playlistUrl, CancellationToken ct)
    {
        for (var depth = 0; depth < 2; depth++)
        {
            // Signed CDN URL: fetch as-is, appending client_id invalidates the signature.
            var (status, body) = await SendWithAuthAsync(playlistUrl, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud HLS playlist HTTP {status}");
                return new List<string>();
            }

            var urls = ParseHlsPlaylist(body, new Uri(playlistUrl));
            if (urls.Count == 0) return urls;
            if (!body.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                return urls; // media playlist

            playlistUrl = urls[0]; // master playlist: descend to media
        }

        return new List<string>();
    }

    /// <summary>m3u8 segment lines: non-comment lines, relative ones resolved against the base.</summary>
    internal static List<string> ParseHlsPlaylist(string m3u8, Uri baseUri)
    {
        var result = new List<string>();
        foreach (var raw in m3u8.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                result.Add(new Uri(baseUri, line).ToString());
            }
            catch (UriFormatException)
            {
                // broken line — skip
            }
        }
        return result;
    }

    /// <summary>
    /// Segment download WITH RETRIES: DPI blocking drops connections probabilistically
    /// (some requests get through), so a dropped/hung segment is retried up to N times —
    /// the download eventually completes instead of dying on one drop. No progress is kept
    /// between attempts: a segment is small (~100-300 KB), refetching is cheaper.
    /// </summary>
    private const int SegmentRetryAttempts = 6;

    private static async Task<byte[]?> DownloadSegmentBytesAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= SegmentRetryAttempts; attempt++)
        {
            try
            {
                var (status, stream, owner) = await SoundCloudHttp.SendStreamAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, url), ct);
                using (owner)
                {
                    if (status != 200 || stream == null)
                    {
                        // Invalid response (404/403) — retrying is pointless: no segment.
                        return null;
                    }
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) when (attempt < SegmentRetryAttempts && !ct.IsCancellationRequested)
            {
                // Body drop/hang (stall timeout) or network failure — retry.
                Logger.Info($"SoundCloud segment retry {attempt}/{SegmentRetryAttempts - 1} ({ex.InnerException?.Message ?? ex.Message})");
                await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the AAC HLS transcoding (mime audio/mp4): prefers quality "sq"
    /// (AAC 160 kbps — what the SoundCloud web player plays), otherwise the first AAC
    /// found ("lq" — 96 kbps). null — no AAC transcoding (old API answers, opus/mp3 only).
    /// </summary>
    internal static string? PickHlsAacUrl(ScTrack track)
    {
        var transcodings = track.Media?.Transcodings;
        if (transcodings == null) return null;

        string? anyAac = null;
        foreach (var t in transcodings)
        {
            if (!string.Equals(t.Format?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Format?.MimeType?.StartsWith(AacMimeType, StringComparison.OrdinalIgnoreCase) != true) continue;
            anyAac ??= t.Url;
            if (string.Equals(t.Quality, "sq", StringComparison.OrdinalIgnoreCase))
                return t.Url;
        }
        return anyAac;
    }

    /// <summary>
    /// The track's AAC variant — same quality as the web player (HLS audio/mp4, usually
    /// 160 kbps vs 128 for progressive mp3): download the init segment and playlist segments,
    /// stitch into one fMP4 and set the total duration (see Fmp4DurationPatcher — without it
    /// Media Foundation has no TotalTime and the timeline/seeking break). null — no AAC / download failed.
    /// </summary>
    public async Task<byte[]?> DownloadHlsAacAsync(ScTrack track, CancellationToken ct)
    {
        var transcodingUrl = PickHlsAacUrl(track);
        if (transcodingUrl == null) return null;

        var playlistUrl = await ResolveTranscodingAsync(transcodingUrl, ct);
        if (playlistUrl == null) return null;

        // Same signed-URL rule as DownloadHlsMp3Async: never append client_id here.
        var playlist = await FetchHlsMediaPlaylistAsync(playlistUrl, ct);
        if (playlist == null || playlist.Segments.Count == 0)
        {
            Logger.Warn("SoundCloud AAC HLS playlist has no segments");
            return null;
        }

        // Init segment is mandatory (ftyp+moov: without it no reader opens the stitch).
        byte[]? initBytes = null;
        if (playlist.InitUrl != null)
        {
            initBytes = await DownloadSegmentBytesAsync(playlist.InitUrl, ct);
            if (initBytes == null)
            {
                Logger.Warn("SoundCloud AAC HLS init segment download failed");
                return null;
            }
        }

        // Segments download with bounded parallelism and are assembled strictly in order:
        // one bad segment breaks the stitch — return null (the cascade falls back to mp3 variants).
        var parts = new byte[playlist.Segments.Count][];
        var gate = new SemaphoreSlim(HlsSegmentParallelism);
        var errors = 0;
        await Task.WhenAll(playlist.Segments.Select(async (segmentUrl, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bytes = await DownloadSegmentBytesAsync(segmentUrl, ct);
                if (bytes == null) Interlocked.Increment(ref errors);
                else parts[index] = bytes;
            }
            finally
            {
                gate.Release();
            }
        }));
        if (errors > 0)
        {
            Logger.Warn($"SoundCloud AAC HLS segments failed: {errors}/{playlist.Segments.Count}");
            return null;
        }

        // Duration: the EXTINF sum is more precise than metadata; fall back to track.DurationMs.
        var durationMs = playlist.TotalSeconds > 0
            ? (long)(playlist.TotalSeconds * 1000)
            : track.DurationMs;

        using var output = new MemoryStream((initBytes?.Length ?? 0) + playlist.Segments.Sum(p => p?.Length ?? 0));
        if (initBytes != null) output.Write(initBytes, 0, initBytes.Length);
        foreach (var part in parts) output.Write(part, 0, part.Length);

        var patched = Fmp4DurationPatcher.Patch(output.ToArray(), durationMs);
        Logger.Info($"SoundCloud AAC HLS assembled: {playlist.Segments.Count} segments, {patched.Length / 1024} KB");
        return patched;
    }

    /// <summary>
    /// Fetches the HLS media playlist body: a master (#EXT-X-STREAM-INF) has one nesting
    /// level (take the first variant); the media playlist is parsed into init segment,
    /// segments and duration.
    /// </summary>
    private async Task<HlsAacPlaylist?> FetchHlsMediaPlaylistAsync(string playlistUrl, CancellationToken ct)
    {
        for (var depth = 0; depth < 2; depth++)
        {
            // Signed CDN URL: fetch as-is, appending client_id invalidates the signature.
            var (status, body) = await SendWithAuthAsync(playlistUrl, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud AAC HLS playlist HTTP {status}");
                return null;
            }

            if (!body.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                return ParseHlsAacPlaylist(body, new Uri(playlistUrl));

            var subs = ParseHlsPlaylist(body, new Uri(playlistUrl));
            if (subs.Count == 0) return null;
            playlistUrl = subs[0];
        }

        return null;
    }

    /// <summary>Parsed AAC HLS media playlist.</summary>
    internal sealed record HlsAacPlaylist(string? InitUrl, List<string> Segments, double TotalSeconds);

    /// <summary>
    /// Media playlist: EXT-X-MAP:URI is the init segment; EXTINF:&lt;dur&gt; before a segment
    /// line is its duration (summed into TotalSeconds); relative URLs resolve against the base.
    /// </summary>
    internal static HlsAacPlaylist ParseHlsAacPlaylist(string m3u8, Uri baseUri)
    {
        string? init = null;
        var segments = new List<string>();
        double totalSeconds = 0;
        string? pendingDuration = null;

        foreach (var raw in m3u8.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MAP:URI=", StringComparison.OrdinalIgnoreCase))
            {
                var value = ExtractQuoted(line);
                if (!string.IsNullOrEmpty(value))
                {
                    try { init = new Uri(baseUri, value).ToString(); }
                    catch (UriFormatException) { /* broken URI — play without init (won't open; null returned above) */ }
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = line.IndexOf(',');
                pendingDuration = line[8..(commaIndex < 0 ? line.Length : commaIndex)].Trim();
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }
            else
            {
                try
                {
                    segments.Add(new Uri(baseUri, line).ToString());
                    if (double.TryParse(pendingDuration, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                        totalSeconds += seconds;
                }
                catch (UriFormatException)
                {
                    // broken line — skip
                }
                pendingDuration = null;
            }
        }

        return new HlsAacPlaylist(init, segments, totalSeconds);
    }

    /// <summary>Quoted value of an HLS tag attribute (#EXT-X-MAP:URI="...").</summary>
    private static string? ExtractQuoted(string line)
    {
        var open = line.IndexOf('"');
        if (open < 0) return null;
        var close = line.IndexOf('"', open + 1);
        return close < 0 ? null : line[(open + 1)..close];
    }

    /// <summary>
    /// Picks an HLS transcoding: prefers mp3 segments (mime audio/mpeg — stitching is valid),
    /// otherwise any hls (the player can't open opus stitching — null on segments).
    /// </summary>
    internal static string? PickHlsMp3Url(ScTrack track)
    {
        var transcodings = track.Media?.Transcodings;
        if (transcodings == null) return null;

        string? anyHls = null;
        foreach (var t in transcodings)
        {
            if (!string.Equals(t.Format?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)) continue;
            anyHls ??= t.Url;
            if (string.Equals(t.Format?.MimeType, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
                return t.Url;
        }
        return anyHls;
    }

    // ========================= Auth ==========================

    /// <summary>Saves web-session cookies (called by the login window after detecting oauth_token).</summary>
    public void SaveSessionCookies(string cookieHeader)
    {
        var file = _auth.Load();
        file.Cookies = cookieHeader;
        _auth.Save(file);
        // New session: the previous "expired" diagnosis no longer holds, and session-dependent
        // caches (the resolve-failure blacklist) must be recomputed — signal listeners.
        _webSessionExpired = false;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Disconnect: delete sc_auth.json. The likes table is cleared by the caller.
    /// The account archive (sc_accounts.json.accounts) is NOT touched — "switch account" may
    /// restore a saved session without re-login.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        _webSessionExpired = false;
    }


    /// <summary>Cookie string from sc_auth.json (internal use and tests only).</summary>
    internal string? GetCookies() => _auth.Load().Cookies;

    // ====================== Network primitives =======================

    /// <summary>
    /// API request with cookies + client_id. On 401/403, refreshes client_id once and retries.
    /// Returns (status, body). Tokens are never logged.
    /// </summary>
    private async Task<(int status, string body)> SendApiAsync(string url, bool forceRefreshClientId, CancellationToken ct)
    {
        var clientId = await _clientIds.GetClientIdAsync(forceRefresh: forceRefreshClientId, ct);
        if (clientId == null)
            throw new SoundCloudApiException("client_id unavailable", 0);

        var urlWithId = AppendClientId(url, clientId);
        var (status, body) = await SendWithAuthAsync(urlWithId, ct);
        if ((status == 401 || status == 403) && !forceRefreshClientId)
        {
            Logger.Warn($"SoundCloud API returned HTTP {status}, refreshing client_id once");
            urlWithId = AppendClientId(url, await _clientIds.GetClientIdAsync(forceRefresh: true, ct)
                ?? throw new SoundCloudApiException("client_id refresh failed", status));
            (status, body) = await SendWithAuthAsync(urlWithId, ct);
        }
        // 401 after a client_id refresh is not a stale id: the web session's oauth_token expired.
        if (status == 401)
            MarkWebSessionExpired();
        return (status, body);
    }

    private async Task<(int status, string body)> SendWithAuthAsync(string url, CancellationToken ct)
    {
        // cookies are read per request (as before); never logged.
        var cookies = _auth.Load().Cookies;
        var oauthToken = ExtractOAuthToken(cookies);
        return await SoundCloudHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookies))
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            // The oauth_token from cookies is duplicated as an Authorization header — some
            // api-v2 endpoints (incl. /me and /users/{id}/likes) answer more reliably with it.
            if (oauthToken != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", oauthToken);
            return request;
        }, ct);
    }

    /// <summary>
    /// oauth_token from a cookie string (the "oauth_token=&lt;value&gt;" pair). null — not found/empty.
    /// The value is never logged — used only for the Authorization header.
    /// </summary>
    internal static string? ExtractOAuthToken(string? cookieHeader)
    {
        if (string.IsNullOrEmpty(cookieHeader)) return null;
        foreach (var pair in cookieHeader.Split(';'))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0) continue;
            if (!string.Equals(pair[..separatorIndex].Trim(), "oauth_token", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = pair[(separatorIndex + 1)..].Trim();
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    internal static string AppendClientId(string url, string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return url;
        if (url.Contains("client_id=", StringComparison.OrdinalIgnoreCase)) return url;
        return url + (url.Contains('?') ? "&" : "?") + "client_id=" + Uri.EscapeDataString(clientId);
    }

    /// <summary>Media-URL path without query (client_id and other tokens are never logged).</summary>
    internal static string MediaPath(string url)
    {
        var queryIndex = url.IndexOf('?');
        return queryIndex < 0 ? url : url[..queryIndex];
    }

    /// <summary>Whether the track has encrypted FairPlay transcodings (cbc/ctr-encrypted-hls,
    /// skd:// keys). If the standard progressive/hls were also rejected by the media endpoint,
    /// the track is DRM-protected (SoundCloud moves MONETIZE tracks to FairPlay) and won't "come alive".</summary>
    public static bool HasEncryptedTranscodings(ScTrack track)
        => track.Media?.Transcodings.Any(t =>
            t.Format?.Protocol?.Contains("encrypted", StringComparison.OrdinalIgnoreCase) == true) == true;

    // ==================== Playable copy of a DRM track ==================

    /// <summary>Title words that mark a copy as a different version (slowed/sped up,
    /// remixes, instrumentals, covers). A word is not applied if it appears in the
    /// original's title (a track-remix searches for remixes).</summary>
    internal static readonly string[] BadReuploadWords =
    {
        "slowed", "sped up", "speed up", "reverb", "nightcore", "8d",
        "remix", "bootleg", "edit", "instrumental", "инструментал",
        "live", "концерт", "concert", "cover", "кавер", "karaoke", "караоке",
        "reaction", "реакция", "type beat", "mashup", "extended mix", "acoustic", "loop"
    };

    /// <summary>
    /// Finds a playable copy of a track on SoundCloud (re-uploaded by other users): DRM
    /// originals (AD_SUPPORTED) play only via Widevine, while copies are usually uploaded as
    /// regular tracks. GET /search/tracks by "artist + title", filters: not the original,
    /// streamable, not SNIPPET/BLOCK, not AD_SUPPORTED, all title words present, no
    /// live/remix markers, duration within ±10s, artist mentioned. Returns up to limit
    /// candidates ordered by duration deviation (full candidate metadata via GetTrackAsync,
    /// which does the final check). Empty list — no confident copies.
    /// </summary>
    public async Task<List<(long Id, string Title)>> SearchReuploadCandidatesAsync(
        string artist, string title, long? durationMs, string excludeScId,
        int limit, CancellationToken ct)
    {
        var result = new List<(long Id, string Title)>();
        if (string.IsNullOrWhiteSpace(title)) return result;

        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{artist.Trim()} {StripBrackets(title)}";
            var (status, json) = await SendApiAsync(
                $"{ApiBase}/search/tracks?q={Uri.EscapeDataString(query)}&limit=25",
                forceRefreshClientId: false, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud reupload search HTTP {status}");
                return result;
            }

            var ranked = RankReuploadCandidates(json, title, artist, durationMs, excludeScId);
            result.AddRange(ranked.Take(Math.Max(1, limit)).Select(x => (x.Id, x.Title)));
            if (result.Count == 0)
                Logger.Info("SoundCloud reupload search: no confident candidates");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud reupload search failed");
        }
        return result;
    }

    /// <summary>Parses /search/tracks output and picks confident copies (testable).</summary>
    internal static List<(long Id, string Title, double Score)> RankReuploadCandidates(
        string searchJson, string title, string artist, long? durationMs, string excludeScId)
    {
        var ranked = new List<(long Id, string Title, double Score)>();
        ScSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ScSearchResponse>(searchJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            Logger.Error(ex, "SoundCloud search JSON parse failed");
            return ranked;
        }
        if (response?.Collection == null) return ranked;

        var cleanTitle = StripBrackets(title);
        var titleTokens = TokenizeWords(cleanTitle);
        var artistTokens = TokenizeWords(artist);
        // Marker "the original is like that itself": a word in the original's title doesn't reject a candidate.
        var scLow = (artist + " " + title).ToLowerInvariant();
        var badWords = BadReuploadWords.Where(w => scLow.Contains(w) == false).ToArray();

        foreach (var t in response.Collection)
        {
            if (t == null || t.Id == 0) continue;
            if (t.Id.ToString() == excludeScId) continue;                      // the original itself
            if (t.Streamable != true) continue;
            if (t.Policy is "SNIPPET" or "BLOCK") continue;                    // Go+/geo
            // A copy with the same ad model hits the same DRM — not a candidate.
            if (string.Equals(t.MonetizationModel, "AD_SUPPORTED", StringComparison.OrdinalIgnoreCase)) continue;

            var candidateTitle = t.Title ?? string.Empty;
            var low = candidateTitle.ToLowerInvariant();
            if (titleTokens.Count > 0 && titleTokens.Any(w => low.Contains(w) == false))
                continue;                                                      // wrong title
            if (badWords.Any(w => low.Contains(w)))
                continue;                                                      // slowed/remix/…
            if (durationMs is { } target && t.DurationMs is { } candDur && Math.Abs(candDur - target) > 10_000)
                continue;                                                      // wrong length

            if (artistTokens.Count > 0)
            {
                var hay = (low + " " + (t.User?.Username ?? "") + " " + (t.FullName ?? "")).ToLowerInvariant();
                if (artistTokens.Count(a => hay.Contains(a)) * 2 < artistTokens.Count)
                    continue;                                                  // artist not mentioned
            }

            // Rank: closer duration + popularity bonus of the copy (mass re-uploads
            // are more reliable than singletons).
            var durDist = durationMs is { } tg && t.DurationMs is { } cd ? Math.Abs(cd - tg) : 0L;
            var score = durDist - Math.Log10(Math.Max(t.PlaybackCount ?? 0, 10)) * 1000;
            ranked.Add((t.Id, candidateTitle, score));
        }

        return ranked.OrderBy(x => x.Score).ToList();
    }

    /// <summary>Strips bracket tails: "jiggy (feat. xaviersobased)" → "jiggy" —
    /// feat/prod parts only hurt the search.</summary>
    internal static string StripBrackets(string title)
    {
        var cleaned = System.Text.RegularExpressions.Regex
            .Replace(title ?? string.Empty, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]", " ")
            .Trim();
        return cleaned.Length > 0 ? cleaned : (title ?? string.Empty).Trim();
    }

    /// <summary>Meaningful words in lowercase (letters/digits, length ≥ 2).</summary>
    private static List<string> TokenizeWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        return System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-zа-яё0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToList();
    }

    // ==================== Response parsing (testable) ==============

    internal static ScLikesResponse? ParseLikesJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScLikesResponse>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            Logger.Error(ex, "SoundCloud likes JSON parse failed");
            return null;
        }
    }

    internal static ScTrack? ParseTrackJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScTrack>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            Logger.Error(ex, "SoundCloud track JSON parse failed");
            return null;
        }
    }

    /// <summary>collection → DB rows; playlists and tracks without id are skipped.</summary>
    internal static List<SoundCloudLikeRow> ExtractLikeRows(ScLikesResponse response, string syncedAt)
    {
        var rows = new List<SoundCloudLikeRow>();
        foreach (var item in response.Collection)
        {
            var track = item.Track;
            if (track == null || track.Id == 0) continue; // playlist and junk

            rows.Add(new SoundCloudLikeRow
            {
                ScId = track.Id.ToString(),
                Title = track.Title ?? string.Empty,
                // username is preferred over full_name (profile names are sometimes empty).
                Artist = !string.IsNullOrWhiteSpace(track.User?.Username)
                    ? track.User!.Username
                    : (!string.IsNullOrWhiteSpace(track.User?.FullName)
                        ? track.User!.FullName!
                        : (track.FullName ?? string.Empty)),
                DurationMs = track.DurationMs,
                ArtworkUrl = BuildArtworkUrl(track.ArtworkUrl) ?? string.Empty,
                PermalinkUrl = track.PermalinkUrl ?? string.Empty,
                Streamable = track.Streamable,
                // the wrapper's created_at is the LIKE date; falls back to the track's upload date.
                LikedAt = item.CreatedAt ?? track.CreatedAt,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    /// <summary>Large artwork: '-large' → '-t500x500' (SoundCloud serves large by default).</summary>
    internal static string? BuildArtworkUrl(string? artworkUrl)
    {
        if (string.IsNullOrEmpty(artworkUrl)) return artworkUrl;
        return artworkUrl.Contains("-large", StringComparison.Ordinal)
            ? artworkUrl.Replace("-large", "-t500x500")
            : artworkUrl;
    }

    /// <summary>
    /// Picks the progressive transcoding (single-file mp3). Missing on HLS-only
    /// tracks — DownloadHlsMp3Async handles those.
    /// </summary>
    internal static string? PickProgressiveUrl(ScTrack track)
    {
        var transcodings = track.Media?.Transcodings;
        if (transcodings == null) return null;

        var progressive = transcodings.FirstOrDefault(t =>
            string.Equals(t.Format?.Protocol, "progressive", StringComparison.OrdinalIgnoreCase));

        return progressive?.Url;
    }
}
