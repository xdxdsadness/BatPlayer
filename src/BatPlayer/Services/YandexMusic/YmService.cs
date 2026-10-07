using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Database;
using BatPlayer.Services;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Yandex Music client. Auth uses an OFFICIAL Yandex ID OAuth token (implicit flow with
/// the Yandex Music app client_id), same principle as the SoundCloud/VK integrations:
/// the login window obtains the token via WebView2, it is stored in ym_auth.json, and
/// every request to the API host api.music.yandex.net carries the header
/// "Authorization: OAuth &lt;token&gt;" (web session cookies get 401 — Session_id cookies
/// do not work). Web client headers (X-Yandex-Music-Client: web) request the web JSON
/// layout, modeled on the mobile client.
///
/// All endpoints require auth: without a token the host answers 401. The catalog is the
/// user's likes (users/{uid}/likes/tracks returns ONLY ids; full objects are fetched in
/// batches via tracks?track-ids); streaming is tracks/{id}/download-info (the variant
/// layout changed — the URL is assembled by the lenient YmJsonParser, see its docs).
///
/// File downloads are NOT implemented: catalog only (metadata), streaming via the disk
/// cache (YmStreamCache), and matching with the local library. The token never reaches
/// the logs — only method, response codes and item counts are logged.
/// </summary>
public sealed class YmService
{
    public const string AuthFileName = "ym_auth.json";

    /// <summary>Yandex Music API host (web client).</summary>
    internal const string ApiBase = "https://api.music.yandex.net";

    /// <summary>Value of X-Yandex-Music-Client/Yandex-Music-Client (web JSON layout).</summary>
    internal const string MusicClientHeader = "web";

    /// <summary>UA for the API and CDN (artworks/streams); internal — set by the YmHttp factory.</summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>Batch size of tracks?track-ids (API limit — modeled on the mobile client).</summary>
    public const int TrackIdBatchSize = 100;

    /// <summary>How long a resolved mp3 URL lives in memory before download-info is re-run.</summary>
    private static readonly TimeSpan StreamUrlTtl = TimeSpan.FromMinutes(60);

    /// <summary>download-info query variants: transports + bitrates in descending order. The
    /// response layout and available bitrates depend on the plan — probe the variants until
    /// at least one computable URL arrives (see YmJsonParser.ParseDownloadInfo).</summary>
    internal static readonly string[] DownloadInfoQueries =
    [
        "transports=encode_info_websonic,pure_d&bitrate=320",
        "transports=encode_info_websonic,pure_d&bitrate=192",
        "transports=encode_info_websonic,pure_d&bitrate=128"
    ];

    /// <summary>Parallelism of the artwork batch download (see SyncArtworksAsync).</summary>
    private const int ArtworkDownloadParallelism = 4;

    private readonly YmAuthService _auth;
    private readonly YmArtworkCache _artworks = new();

    /// <summary>ym_id → temporary mp3 URL (from the last download-info). URLs are not written to the DB.</summary>
    private readonly Dictionary<string, (string Url, DateTime FetchedUtc)> _urlCache = new(StringComparer.Ordinal);

    /// <summary>URL cache lock (filled and read by the player resolver).</summary>
    private readonly object _urlLock = new();

    /// <summary>Sync progress: (done, estimated total).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    public YmService(string authFilePath) => _auth = new YmAuthService(authFilePath);

    /// <summary>
    /// Whether the account is connected: the session file exists AND holds a non-empty
    /// OAuth token. No network check is performed — a revoked token surfaces on the first
    /// request.
    /// </summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(_auth.Load().AccessToken);

    /// <summary>Account uid from the session file ("" — unknown); for the settings status.</summary>
    public string GetSavedUid() => _auth.Load().Uid ?? string.Empty;

    /// <summary>Account display name from the session file ("" — unknown); for the settings status.</summary>
    public string GetSavedDisplayName() => _auth.Load().DisplayName ?? string.Empty;

    // ============================ Session ============================

    /// <summary>
    /// Saves the OAuth token after a successful sign-in (called by the login window).
    /// The token itself is never logged. uid/displayName come from account/status
    /// (may be empty — the sync then fills them in itself).
    /// </summary>
    public void SaveSessionOAuth(string accessToken, string? uid, string? displayName)
    {
        var file = _auth.Load();
        file.AccessToken = accessToken;
        file.Uid = uid ?? string.Empty;
        file.DisplayName = displayName ?? string.Empty;
        file.SavedAt = DateTime.UtcNow.ToString("o");
        _auth.Save(file);
        Logger.Info($"Yandex Music: OAuth token saved (uid={(uid?.Length > 0 ? uid : "unknown")})");
    }

    /// <summary>
    /// Marks the session invalid (API returned 401/403): the OAuth token is cleared,
    /// uid/displayName are kept (useful on re-login). The file is not deleted — the
    /// Connect button reopens the login window.
    /// </summary>
    public void InvalidateSession()
    {
        var file = _auth.Load();
        file.AccessToken = null;
        _auth.Save(file);
        Logger.Warn("Yandex Music OAuth token invalidated — sign in again");
    }

    /// <summary>Disconnect: delete ym_auth.json and the URL cache. The caller clears the table.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        lock (_urlLock) _urlCache.Clear();
    }

    /// <summary>Date of the last successful sync (from ym_auth.json); null — never synced.</summary>
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

    // ======================== Network primitives =====================

    /// <summary>GET relative to ApiBase with the OAuth header (token never logged).</summary>
    private async Task<(int Status, string Body)> GetAsync(string pathAndQuery, string accessToken, CancellationToken ct)
        => await SendAsync(HttpMethod.Get, pathAndQuery, accessToken, ct);

    /// <summary>Arbitrary method (POST/DELETE — likes) with the OAuth header.</summary>
    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string pathAndQuery, string accessToken, CancellationToken ct)
        => await YmHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(method, ApiBase + pathAndQuery);
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.yandex.ru/");
            return request;
        }, ct);

    /// <summary>JSON body on success; non-2xx → YmApiException with the code (401/403 — session).</summary>
    private static string EnsureSuccess(int status, string body, string what)
    {
        if (status == 200) return body;
        throw new YmApiException($"{what} failed with HTTP {status}", status);
    }

    // ========================= Account ==============================

    /// <summary>
    /// Account by the saved OAuth token (uid + display name). null — no token or the
    /// response did not match (401/other): the settings status shows the name saved at
    /// sign-in.
    /// </summary>
    public async Task<YmAccountInfo?> GetAccountStatusAsync(CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        try
        {
            return await FetchAccountInfoAsync(accessToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A network failure of the account check must not crash the caller.
            Logger.Error(ex, "Yandex Music account status failed");
            return null;
        }
    }

    /// <summary>
    /// Account by an arbitrary OAuth token (the login window calls it BEFORE saving the
    /// session, to write uid/displayName into the file right away). null — no match.
    /// </summary>
    internal static async Task<YmAccountInfo?> FetchAccountInfoAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await YmHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/account/status");
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.yandex.ru/");
            return request;
        }, ct);
        return status == 200 ? YmJsonParser.ParseAccountStatus(body) : null;
    }

    /// <summary>
    /// Ensures a saved uid: it is taken from the TOKEN STATUS, not from the file — the file
    /// may belong to another account, and the sync would then read another/old account's
    /// likes (reported as "likes update only after re-login"). DisplayName is updated too.
    /// On a network failure of the status check, the saved uid is the fallback.
    /// </summary>
    private async Task<string> EnsureUidAsync(CancellationToken ct)
    {
        var info = await GetAccountStatusAsync(ct);
        if (info == null)
        {
            var fallback = GetSavedUid();
            if (fallback.Length > 0)
            {
                Logger.Warn("Yandex Music: account/status failed — using saved uid");
                return fallback;
            }
            throw new YmApiException("account uid unavailable", 0);
        }

        var file = _auth.Load();
        if (file.Uid != info.Uid)
            Logger.Info($"Yandex Music: token account uid {file.Uid} -> {info.Uid}");
        file.Uid = info.Uid;
        if (info.DisplayName.Length > 0)
            file.DisplayName = info.DisplayName;
        _auth.Save(file);
        return info.Uid;
    }

    // ======================== Synchronization =========================

    /// <summary>
    /// Likes sync: likes/tracks (ids only) → batches of tracks?track-ids (by
    /// <see cref="TrackIdBatchSize"/>) → DB upsert → artwork download. Returns the number
    /// of saved tracks. Throws <see cref="YmApiException"/> (401/403 — token revoked; the
    /// service resets the session itself) — the VM shows a clear message.
    /// </summary>
    public async Task<int> SyncLikedTracksAsync(YmTracksRepository repository, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var uid = await EnsureUidAsync(ct);

        // 1) Likes — id + like time (see ParseLikeEntries).
        var (likesStatus, likesBody) = await GetAsync($"/users/{uid}/likes/tracks", accessToken, ct);
        var likeEntries = YmJsonParser.ParseLikeEntries(EnsureSuccess(likesStatus, likesBody, "likes/tracks"));
        Logger.Info($"Yandex Music sync: uid={uid}, likes={likeEntries.Count}");

        var syncedAt = DateTime.UtcNow.ToString("o");
        var total = likeEntries.Count;
        var saved = 0;
        SyncProgress?.Invoke(this, (0, total));

        // Catalog order keeps liked_at (like time from the API): "recent likes on top",
        // also across repeated syncs — new likes are not lost at the end of a 400+ list.
        var likedAtById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in likeEntries)
            likedAtById.TryAdd(e.Id.Split(':')[0], e.LikedAt);

        // 2) Full objects — in batches of 100 ids (like order is preserved).
        foreach (var chunk in Chunk(likeEntries.Select(e => e.Id).ToList(), TrackIdBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var query = "/tracks?track-ids=" + Uri.EscapeDataString(string.Join(',', chunk)) + "&lang=ru";
            var (status, body) = await GetAsync(query, accessToken, ct);
            var tracks = YmJsonParser.ParseTracksResponse(EnsureSuccess(status, body, "tracks"));

            var rows = ExtractRows(tracks, syncedAt);
            foreach (var row in rows)
                row.LikedAt = likedAtById.GetValueOrDefault(row.YmId);
            if (rows.Count > 0)
                await repository.UpsertBatchAsync(rows);
            saved += rows.Count;

            // Progress counts processed ids (the API may not return some of them).
            SyncProgress?.Invoke(this, (Math.Min(total, saved + Math.Max(0, chunk.Count - tracks.Count)), total));
        }

        // 2b) Un-liked tracks are removed: the page reflects current likes,
        //     not a history of everything ever synced.
        var removed = await repository.DeleteNotInAsync(
            likeEntries.Select(e => e.Id.Split(':')[0]));

        SetLastSyncedUtc(DateTime.UtcNow);
        Logger.Info($"Yandex Music sync done: saved={saved}, removed={removed}");

        // 3) Artworks are a separate batch AFTER the main upsert: the metadata is already
        //    saved, so an artwork failure does not fail the finished sync.
        await SyncArtworksAsync(repository, ct);

        return saved;
    }

    /// <summary>
    /// Like/unlike a track in the Yandex Music ACCOUNT: POST users/{uid}/likes/tracks/add
    /// (trackId parameter) and .../remove (track-ids parameter). The player's heart button
    /// for YM tracks calls this — after a sync the track appears on the YM page and in
    /// "Favorites" (previously the heart silently wrote to the local DB under a negative
    /// runtime id; no row existed and the like was lost). true — the API confirmed. Bare
    /// POST/DELETE on /likes/tracks get 405 Method Not Allowed, and /add with a track-ids
    /// parameter gets 400 "trackId: Parameter value is not set".
    /// </summary>
    public async Task<bool> SetTrackLikedAsync(string ymId, bool liked, CancellationToken ct = default)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(ymId))
            return false;

        try
        {
            var uid = await EnsureUidAsync(ct);
            var id = ymId.Split(':')[0];
            var action = liked ? $"add?trackId={Uri.EscapeDataString(id)}"
                               : $"remove?track-ids={Uri.EscapeDataString(id)}";
            var (status, _) = await SendAsync(HttpMethod.Post,
                $"/users/{uid}/likes/tracks/{action}", accessToken, ct);
            if (status is >= 200 and < 300) return true;
            Logger.Warn($"Yandex Music like toggle failed: HTTP {status}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music like toggle failed");
            return false;
        }
    }

    /// <summary>
    /// Similar Yandex Music tracks (GET /tracks/{ymId}/similar) — the similarity graph
    /// built by Yandex's recommender. The basis of "My Wave": candidates the user does
    /// not have yet are gathered from library seeds.
    /// Throws <see cref="YmApiException"/> (401/403 — token revoked).
    /// </summary>
    public async Task<List<YmTrackDto>> GetSimilarTracksAsync(string ymId, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync($"/tracks/{ymId}/similar?lang=ru", accessToken, ct);
        return YmJsonParser.ParseSimilarTracks(EnsureSuccess(status, body, "similar"), ymId);
    }

    /// <summary>
    /// Track search by "artist + title" (GET /search?type=track) — matches seeds from
    /// VK/local library/SC to ym_id so they can be fed into the similar endpoint.
    /// Returns up to limit results (the API caps the output itself; the rest is trimmed
    /// here). Throws YmApiException.
    /// </summary>
    public async Task<List<YmTrackDto>> SearchTracksAsync(string text, int limit, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        // The page parameter is REQUIRED (without it the API answers 400 "Parameters
        // requirements are not met: [page: Parameter value is not set]") — verified 2026-09.
        var query = "/search?text=" + Uri.EscapeDataString(text)
                    + "&type=track&lang=ru&page=0";
        var (status, body) = await GetAsync(query, accessToken, ct);
        var tracks = YmJsonParser.ParseSearchTracks(EnsureSuccess(status, body, "search"));
        return limit > 0 && tracks.Count > limit ? tracks.Take(limit).ToList() : tracks;
    }

    /// <summary>
    /// Artist search by name (GET /search?type=artist) — resolves play_log names to
    /// ym artist ids for /artists/{id}/… (mapping cache in wave_seed_map).
    /// Throws YmApiException.
    /// </summary>
    public async Task<List<YmArtistDto>> SearchArtistsAsync(string text, int limit, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var query = "/search?text=" + Uri.EscapeDataString(text)
                    + "&type=artist&lang=ru&page=0";
        var (status, body) = await GetAsync(query, accessToken, ct);
        var artists = YmJsonParser.ParseSearchArtists(EnsureSuccess(status, body, "search-artists"));
        return limit > 0 && artists.Count > limit ? artists.Take(limit).ToList() : artists;
    }

    /// <summary>
    /// Artist tracks (GET /artists/{id}/tracks?page=…&amp;pageSize=limit) — the artist's
    /// catalog regardless of the user's library: "tracks by artists I listen to but have
    /// not added yet". The catalog is paged: the deeper it goes, the more material for
    /// rotation between mixes. Throws YmApiException.
    /// </summary>
    public async Task<List<YmTrackDto>> GetArtistTracksAsync(string artistId, int limit, int page, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync(
            $"/artists/{artistId}/tracks?page={page}&pageSize={Math.Clamp(limit, 1, 50)}", accessToken, ct);
        return YmJsonParser.ParseArtistTracks(EnsureSuccess(status, body, "artist-tracks"));
    }

    /// <summary>
    /// Similar artists (GET /artists/{id}/similar) — up to 50 artists of the same scene
    /// per Yandex's graph. Throws YmApiException.
    /// </summary>
    public async Task<List<YmArtistDto>> GetSimilarArtistsAsync(string artistId, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync($"/artists/{artistId}/similar", accessToken, ct);
        return YmJsonParser.ParseSimilarArtists(EnsureSuccess(status, body, "similar-artists"), artistId);
    }

    /// <summary>
    /// Artist audience (GET /artists/{id}/brief-info → result.stats.listeners) — a filter
    /// for no-names and "AI tracks" when picking wave novelty. null — the API did not
    /// provide the field. Throws YmApiException.
    /// </summary>
    public async Task<long?> GetArtistListenersAsync(string artistId, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync($"/artists/{artistId}/brief-info", accessToken, ct);
        return YmJsonParser.ParseArtistListeners(EnsureSuccess(status, body, "artist-listeners"));
    }

    /// <summary>
    /// Artist radio feed (GET /rotor/station/artist:{id}/tracks) — a batch (~5 tracks)
    /// of "similar sound" from Yandex Rotor. Throws YmApiException.
    /// </summary>
    public async Task<List<YmTrackDto>> GetArtistRadioTracksAsync(string artistId, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync(
            $"/rotor/station/artist:{artistId}/tracks", accessToken, ct);
        return YmJsonParser.ParseRadioTracks(EnsureSuccess(status, body, "artist-radio"));
    }

    /// <summary>
    /// Track artwork in the local cache artworks_cache/ym_{ym_id}.jpg (already downloaded —
    /// path without network; otherwise downloads by coverUri). null — no URL or the
    /// download failed. The Wave and the Yandex Music page share this artwork cache.
    /// </summary>
    public async Task<string?> EnsureArtworkAsync(string ymId, string? coverUri, CancellationToken ct)
        => await _artworks.EnsureDownloadedAsync(
            ArtworkIdPrefix + ymId, YmJsonParser.BuildArtworkUrl(coverUri), ct);

    /// <summary>Parsed API tracks → DB rows (pure function, unit-tested).</summary>
    internal static List<YmTrackRow> ExtractRows(IReadOnlyList<YmTrackDto> tracks, string syncedAt)
    {
        var rows = new List<YmTrackRow>(tracks.Count);
        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.Id)) continue;

            rows.Add(new YmTrackRow
            {
                YmId = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                DurationMs = Math.Max(0, track.DurationMs),
                ArtworkUrl = YmJsonParser.BuildArtworkUrl(track.CoverUri, "300x300") ?? string.Empty,
                Available = track.Available,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    /// <summary>
    /// Batch download of all YM track artworks to artworks_cache/ym_{ym_id}.jpg (see
    /// <see cref="YmArtworkCache"/>): up to <see cref="ArtworkDownloadParallelism"/> parallel
    /// downloads, skipping already downloaded files and tracks without artwork_url. Per-file
    /// errors are logged and skipped. DB results are written sequentially (one connection).
    /// </summary>
    private async Task SyncArtworksAsync(YmTracksRepository repository, CancellationToken ct)
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
                    var path = await _artworks.EnsureDownloadedAsync(ArtworkIdPrefix + row.YmId, row.ArtworkUrl, ct);
                    return (row.YmId, Path: path, Existing: row.ArtworkLocalPath);
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var (ymId, path, existing) in results)
            {
                if (path != null && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    await repository.SetArtworkLocalPathAsync(ymId, path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Artworks are auxiliary to the sync: a network failure must not affect the metadata.
            Logger.Error(ex, "Yandex Music artwork batch download failed");
        }
    }

    /// <summary>Prefix of YM artwork files in the shared artworks_cache directory.</summary>
    internal const string ArtworkIdPrefix = "ym_";

    // ===================== OAuth Device Flow (sign-in) ==================

    /// <summary>Official OAuth credentials of the Yandex Music app (Android client,
    /// public, not a secret — as in the unofficial yandex-music-api). This exact pair is
    /// registered at oauth.yandex.ru: an implicit redirect with another circulating public
    /// client_id returns 400 "Unknown application with this client_id", so sign-in goes
    /// through Device Flow — a confirmation code at oauth.yandex.ru/device.</summary>
    internal const string OAuthClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    internal const string OAuthClientSecret = "53bc75238f0c4d08a118e51fe9203300";

    /// <summary>Yandex ID endpoints for Device Flow (separate host, not ApiBase).</summary>
    internal const string OAuthDeviceCodeUrl = "https://oauth.yandex.ru/device/code";
    internal const string OAuthTokenUrl = "https://oauth.yandex.ru/token";

    /// <summary>Code confirmation page if Yandex did not send verification_url.</summary>
    internal const string DefaultVerificationUrl = "https://oauth.yandex.ru/device";

    /// <summary>Device Flow parameter defaults if the response lacked expires_in/interval.</summary>
    internal const int DefaultExpiresInSeconds = 300;
    internal const int DefaultPollIntervalSeconds = 5;

    /// <summary>device_name under which the token will appear in the Yandex ID device list.</summary>
    internal const string DeviceName = "BatPlayer";

    /// <summary>
    /// Requests a device code (Device Flow step 1): the user enters the UserCode at the
    /// VerificationUrl while the player polls the token via <see cref="PollDeviceTokenAsync"/>.
    /// Throws <see cref="YmApiException"/> on network failure/non-200.
    /// </summary>
    public async Task<YmDeviceCode> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var (status, body) = await YmHttp.SendWithFailoverAsync(() => new HttpRequestMessage(
            HttpMethod.Post, OAuthDeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OAuthClientId,
                ["device_id"] = GenerateDeviceId(),
                ["device_name"] = DeviceName
            })
        }, ct);

        if (status != 200)
            throw new YmApiException($"device/code failed with HTTP {status}", status);

        return YmJsonParser.ParseDeviceCode(body)
               ?? throw new YmApiException("device/code returned unparseable body", 0);
    }

    /// <summary>
    /// Single token poll (Device Flow step 2). The caller interprets the result:
    /// AccessToken — success; IsPending — wait for the next tick; IsSlowDown — increase
    /// the interval; IsExpired/IsDenied — stop polling. The token is never logged.
    /// </summary>
    public async Task<YmTokenResult> PollDeviceTokenAsync(string deviceCode, CancellationToken ct)
    {
        var (status, body) = await YmHttp.SendWithFailoverAsync(() => new HttpRequestMessage(
            HttpMethod.Post, OAuthTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "device_code",
                ["code"] = deviceCode,
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret
            })
        }, ct);

        var result = YmJsonParser.ParseTokenResponse(body);
        // 200 without access_token and 400 without error — the response layout changed;
        // the status is kept for the caller's log.
        if (status != 200 && result.ErrorCode == null)
            result = new YmTokenResult { ErrorCode = "invalid_response" };
        return result;
    }

    /// <summary>Random device_id (10 latin letters/digits — as in yandex-music-api):
    /// the token will appear in the account's device list under this id.</summary>
    internal static string GenerateDeviceId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    // ============================ Streaming ==========================

    /// <summary>
    /// Direct mp3 URL of a track for streaming: session cache (TTL) → GET tracks/{id}/download-info
    /// (first without parameters — the 2025+ layout, then transports/bitrate variants in
    /// descending order). The modern response returns downloadInfoUrl — an XML descriptor
    /// from which the final URL https://{host}/get-mp3/{s}/{ts}{path} is built. URLs are
    /// temporary and not saved to the DB. null — the URL could not be obtained (no token,
    /// track unavailable); 401/403 reset the session and throw <see cref="YmApiException"/>.
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(string ymId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ymId)) return null;

        if (TryGetFreshUrl(ymId, out var url)) return url;

        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        // Query layouts: the modern one first (no parameters), then legacy variants.
        var queries = new List<string> { string.Empty };
        queries.AddRange(DownloadInfoQueries.Select(q => $"?{q}"));

        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();

            int status;
            string body;
            try
            {
                (status, body) = await GetAsync($"/tracks/{ymId}/download-info{query}", accessToken, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Network unavailable — the other bitrates will not help.
                Logger.Error(ex, $"Yandex Music download-info failed ({ymId})");
                return null;
            }

            if (YmApiException.IsSessionError(status))
            {
                InvalidateSession();
                throw new YmApiException($"download-info returned HTTP {status} — token expired", status);
            }
            if (status != 200)
            {
                // Previously a non-200 was swallowed silently — now the failed resolve is visible.
                Logger.Warn($"Yandex Music download-info HTTP {status} ({ymId}, query=\"{query}\")");
                continue;
            }

            var options = YmJsonParser.ParseDownloadInfo(body);
            var best = PickBestOption(options);
            if (best == null) continue;

            var finalUrl = best.DownloadInfoUrl.Length > 0
                ? await ResolveDescriptorUrlAsync(best.DownloadInfoUrl, ct)
                : best.Url;
            if (string.IsNullOrEmpty(finalUrl)) continue;

            CacheStreamUrl(ymId, finalUrl);
            return finalUrl;
        }

        Logger.Warn($"Yandex Music download-info produced no playable option ({ymId})");
        return null;
    }

    /// <summary>
    /// Fetches the XML descriptor at downloadInfoUrl and builds the final mp3 URL.
    /// null — the descriptor was not obtained/parsed (logged; the next variant is tried).
    /// </summary>
    private async Task<string?> ResolveDescriptorUrlAsync(string descriptorUrl, CancellationToken ct)
    {
        try
        {
            var (status, body) = await YmHttp.SendWithFailoverAsync(
                () => new HttpRequestMessage(HttpMethod.Get, descriptorUrl), ct);
            if (status != 200)
            {
                Logger.Warn($"Yandex Music download-info descriptor HTTP {status}");
                return null;
            }

            var info = YmJsonParser.ParseDownloadInfoXml(body);
            return info == null
                ? null
                : YmJsonParser.BuildDownloadUrlFromXml(info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music download-info descriptor fetch failed");
            return null;
        }
    }

    /// <summary>Best URL variant: mp3 with the highest bitrate; otherwise any non-empty one.
    /// A variant may carry either a ready Url or DownloadInfoUrl (the descriptor is resolved
    /// by the service) — in the modern download-info layout Url is empty and only the
    /// descriptor is present.</summary>
    internal static YmDownloadOption? PickBestOption(IReadOnlyList<YmDownloadOption> options)
    {
        YmDownloadOption? best = null;
        foreach (var option in options)
        {
            if (string.IsNullOrEmpty(option.Url) && string.IsNullOrEmpty(option.DownloadInfoUrl)) continue;
            var isMp3 = string.Equals(option.Codec, "mp3", StringComparison.OrdinalIgnoreCase);
            var bestIsMp3 = best != null && string.Equals(best.Codec, "mp3", StringComparison.OrdinalIgnoreCase);
            if (best == null
                || (isMp3 && !bestIsMp3)
                || (isMp3 == bestIsMp3 && option.Bitrate > best.Bitrate))
            {
                best = option;
            }
        }
        return best;
    }

    private bool TryGetFreshUrl(string ymId, out string url)
    {
        lock (_urlLock)
        {
            if (_urlCache.TryGetValue(ymId, out var entry)
                && DateTime.UtcNow - entry.FetchedUtc < StreamUrlTtl)
            {
                url = entry.Url;
                return true;
            }
        }
        url = string.Empty;
        return false;
    }

    /// <summary>Caches a track's temporary URL (TTL in <see cref="StreamUrlTtl"/>).</summary>
    private void CacheStreamUrl(string ymId, string url)
    {
        lock (_urlLock) _urlCache[ymId] = (url, DateTime.UtcNow);
    }

    // ========================== Utilities ==============================

    internal static List<List<string>> Chunk(IReadOnlyList<string> ids, int size)
    {
        var chunks = new List<List<string>>();
        for (var i = 0; i < ids.Count; i += size)
            chunks.Add(ids.Skip(i).Take(size).ToList());
        return chunks;
    }
}
