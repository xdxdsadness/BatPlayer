using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;

using BatPlayer.Localization;

namespace BatPlayer.Services.SoundCloud;

/// <summary>Official API streaming URL pair (AAC 160 — best, MP3 128 — fallback).
/// preview_mp3_128_url (the 30-second snippet) is deliberately unused: playing a snippet
/// under full metadata is exactly "right title, wrong sound".</summary>
public sealed record ScApiStreamUrls(string? HlsAac160, string? HlsMp3128)
{
    public bool HasAny => HlsAac160 != null || HlsMp3128 != null;
}

/// <summary>
/// Official api.soundcloud.com client: track streams (official, work for anything the
/// account can access, incl. Go+), related tracks (SC recommendations), likes.
/// Token via SoundCloudOfficialAuth with auto-refresh on 401/expiry.
/// Network via the shared static SoundCloudHttp layer (direct with proxy fallback).
/// </summary>
public sealed class SoundCloudOfficialApi
{
    private const string ApiBase = "https://api.soundcloud.com";

    private readonly SoundCloudOfficialAuth _auth;

    /// <summary>The API disallowed this app entirely (403 "Access to this API has
    /// been disallowed" on /streams): formally connected, but no track will get
    /// official streams until the client is unblocked. A 200 clears it.</summary>
    private volatile bool _disallowed;
    private DateTime _disallowedAtUtc;

    /// <summary>How often to re-probe official streams after a block: if SoundCloud
    /// unblocks the client, the flag is cleared by the first probing 200.</summary>
    private static readonly TimeSpan DisallowedReprobeInterval = TimeSpan.FromMinutes(30);

    public SoundCloudOfficialApi(SoundCloudOfficialAuth auth) => _auth = auth;

    /// <summary>true — api.soundcloud.com returned a 403 "disallowed": the official source
    /// is dead for this session (detected via the response body, not every 403 — a
    /// per-track/permission denial also arrives as 403).</summary>
    public bool IsDisallowed => _disallowed;

    /// <summary>Skip official-stream attempts: the API is blocked and less than the interval
    /// has passed since the last probe. After the interval the request goes out again —
    /// unblocking is picked up automatically.</summary>
    public bool ShouldSkipStreamsProbe
        => _disallowed && DateTime.UtcNow - _disallowedAtUtc < DisallowedReprobeInterval;

    public bool IsConnected => !string.IsNullOrEmpty(_auth.Load().AccessToken);

    /// <summary>Live access_token for fetching official API playlists
    /// (SoundCloudService assembles the HLS). null — not connected.</summary>
    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => _auth.GetValidAccessTokenAsync(ct);

    /// <summary>URN from scId: scId is the numeric SC track id ("123456").</summary>
    private static string TrackUrn(string scId) => $"soundcloud:tracks:{scId}";

    /// <summary>GET /tracks/{urn}/streams — official URLs (AAC 160 and MP3 128).
    /// On 401, refreshes the access_token once (refresh grant) and retries.
    /// null — not connected / no rights / track unavailable (details in the log).</summary>
    public async Task<ScApiStreamUrls?> GetStreamUrlsAsync(string scId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(scId) || !IsConnected) return null;

        var (status, body) = await SendAuthorizedAsync(
            $"{ApiBase}/tracks/{TrackUrn(scId)}/streams", forceRefresh: false, ct);
        if (status == 401)
        {
            // Access_token expired (lives ~1h): refresh and one retry.
            Logger.Info("SoundCloud official streams 401 — refreshing access token");
            (status, body) = await SendAuthorizedAsync(
                $"{ApiBase}/tracks/{TrackUrn(scId)}/streams", forceRefresh: true, ct);
        }
        if (status != 200)
        {
            // A 403 with "disallowed" in the body blocks the API client itself (all tracks),
            // not a per-track permission denial: remember it for the fallback logic.
            if (status == 403 && body.Contains("disallowed", StringComparison.OrdinalIgnoreCase))
            {
                if (!_disallowed)
                    Logger.Warn("SoundCloud official API disallowed for this client — official streams are dead");
                _disallowed = true;
                _disallowedAtUtc = DateTime.UtcNow;
            }
            Logger.Warn($"SoundCloud official streams HTTP {status} (scId={scId})");
            return null;
        }
        _disallowed = false;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? Get(string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        var urls = new ScApiStreamUrls(Get("hls_aac_160_url"), Get("hls_mp3_128_url"));
        var preview = Get("preview_mp3_128_url");
        // "Won't play" diagnostics: preview-only streams mean the account/app
        // lacks full-version rights (Go+ / regional restrictions), not a network failure.
        if (!urls.HasAny)
            Logger.Warn($"SoundCloud official streams: full URLs absent " +
                        $"(preview={preview != null}, scId={scId}) — no playback rights");
        return urls;
    }

    /// <summary>GET /tracks/{urn}/related — related tracks (SC recommendations).
    /// Returns up to limit tracks the account can stream.</summary>
    public async Task<List<ScTrack>?> GetRelatedTracksAsync(string scId, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(scId) || !IsConnected) return null;

        var (status, body) = await SendAuthorizedAsync(
            $"{ApiBase}/tracks/{TrackUrn(scId)}/related?limit={Math.Clamp(limit, 1, 50)}",
            forceRefresh: false, ct);
        if (!IsSuccess(status)) return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("collection", out var col)) return null;

        var list = new List<ScTrack>();
        foreach (var t in col.EnumerateArray())
        {
            if (t.TryGetProperty("kind", out var kind) && kind.GetString() != "track") continue;
            if (!t.TryGetProperty("id", out var idEl)) continue;
            long id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : long.Parse(idEl.GetString()!);
            if (id == 0) continue;

            var title = t.TryGetProperty("title", out var tt) ? tt.GetString() : null;
            string? artist = null;
            if (t.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un))
                artist = un.GetString();
            int? durationMs = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt32() : null;
            string? artwork = t.TryGetProperty("artwork_url", out var aw) ? aw.GetString() : null;
            bool streamable = t.TryGetProperty("streamable", out var st) && st.ValueKind == JsonValueKind.Number
                ? st.GetInt32() != 0
                : t.TryGetProperty("streamable", out var stb) && stb.ValueKind == JsonValueKind.True;

            list.Add(new ScTrack
            {
                Id = id,
                Title = title ?? Loc.Get("UntitledTrack"),
                User = new ScUser { Username = artist ?? "" },
                DurationMs = durationMs ?? 0,
                ArtworkUrl = artwork,
                Streamable = streamable
            });
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>GET /me/likes/tracks — the account's official likes (instead of parsing
    /// the unofficial endpoint, whose JSON periodically broke).</summary>
    public async Task<List<ScTrack>?> GetLikedTracksAsync(int limit, CancellationToken ct = default)
    {
        if (!IsConnected) return null;

        var (status, body) = await SendAuthorizedAsync(
            $"{ApiBase}/me/likes/tracks?limit={Math.Clamp(limit, 1, 200)}",
            forceRefresh: false, ct);
        if (!IsSuccess(status)) return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("collection", out var col)) return null;

        var list = new List<ScTrack>();
        foreach (var item in col.EnumerateArray())
        {
            if (!item.TryGetProperty("track", out var t)) continue;
            if (!t.TryGetProperty("id", out var idEl)) continue;
            long id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : long.Parse(idEl.GetString()!);
            if (id == 0) continue;

            var title = t.TryGetProperty("title", out var tt) ? tt.GetString() : null;
            string? artist = null;
            if (t.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un))
                artist = un.GetString();
            int? durationMs = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt32() : null;
            string? artwork = t.TryGetProperty("artwork_url", out var aw) ? aw.GetString() : null;

            list.Add(new ScTrack
            {
                Id = id,
                Title = title ?? Loc.Get("UntitledTrack"),
                User = new ScUser { Username = artist ?? "" },
                DurationMs = durationMs ?? 0,
                ArtworkUrl = artwork,
                Streamable = true
            });
            if (list.Count >= limit) break;
        }
        return list;
    }

    // ========================= Network primitives ====================

    private static bool IsSuccess(int status) => status is >= 200 and < 300;

    /// <summary>GET with Authorization: OAuth. 401 + forceRefresh — refresh the token before the request.
    /// Body as a string; network retries (direct↔proxy) are handled by SoundCloudHttp.</summary>
    private async Task<(int Status, string Body)> SendAuthorizedAsync(
        string url, bool forceRefresh, CancellationToken ct)
    {
        var token = await _auth.GetValidAccessTokenAsync(ct, forceRefresh);
        if (string.IsNullOrEmpty(token)) return (401, "");

        return await SoundCloudHttp.SendWithFailoverAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", token);
            return req;
        }, ct);
    }
}
