using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;

namespace BatPlayer.Services.SoundCloud;

/// <summary>Пара URL стриминга официального API (AAC 160 — лучший, MP3 128 — фолбэк).
/// preview_mp3_128_url (30-секундный сниппет) сознательно не используется: играть отрывок
/// под полными метаданными — то самое «название то, а звук не тот».</summary>
public sealed record ScApiStreamUrls(string? HlsAac160, string? HlsMp3128)
{
    public bool HasAny => HlsAac160 != null || HlsMp3128 != null;
}

/// <summary>
/// Клиент официального api.soundcloud.com: стримы трека (официальные, работают для
/// всего, что доступно аккаунту — вкл. Go+), related-треки (рекомендации SC),
/// лайки. Токен — через SoundCloudOfficialAuth с авто-рефрешом при 401/истечении.
/// Сеть — общий статический слой SoundCloudHttp (direct с фолбэком на прокси).
/// </summary>
public sealed class SoundCloudOfficialApi
{
    private const string ApiBase = "https://api.soundcloud.com";

    private readonly SoundCloudOfficialAuth _auth;

    /// <summary>API запретил доступ приложению целиком (403 "Access to this API has
    /// been disallowed" на /streams): подключение формально есть, но ни один трек
    /// официальных стримов не получит, пока клиент не разблокируют. 200 — сбрасывает.</summary>
    private volatile bool _disallowed;
    private DateTime _disallowedAtUtc;

    /// <summary>Как часто после блокировки можно снова пробовать официальный стрим:
    /// если SoundCloud разблокирует клиента, флаг снимется первым же пробным 200.</summary>
    private static readonly TimeSpan DisallowedReprobeInterval = TimeSpan.FromMinutes(30);

    public SoundCloudOfficialApi(SoundCloudOfficialAuth auth) => _auth = auth;

    /// <summary>true — api.soundcloud.com отдал 403 «disallowed»: официальный источник
    /// мёртв для этой сессии (проверяется по телу ответа, не по каждому 403 — отказ
    /// конкретного трека/права тоже приходит как 403).</summary>
    public bool IsDisallowed => _disallowed;

    /// <summary>Пропустить попытку официальных стримов: API заблокирован и с последнего
    /// пробного запроса прошло меньше интервала. По прошествии интервала запрос опять
    /// уходит — разблокировка подхватится автоматически.</summary>
    public bool ShouldSkipStreamsProbe
        => _disallowed && DateTime.UtcNow - _disallowedAtUtc < DisallowedReprobeInterval;

    public bool IsConnected => !string.IsNullOrEmpty(_auth.Load().AccessToken);

    /// <summary>Живой access_token для скачивания плейлистов официального API
    /// (склейкой HLS занимается SoundCloudService). null — не подключено.</summary>
    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => _auth.GetValidAccessTokenAsync(ct);

    /// <summary>URN из scId: scId — это числовой id трека SC ("123456").</summary>
    private static string TrackUrn(string scId) => $"soundcloud:tracks:{scId}";

    /// <summary>GET /tracks/{urn}/streams — официальные URL (AAC 160 и MP3 128).
    /// При 401 один раз обновляет access_token (refresh-грант) и повторяет.
    /// null — не подключено/нет права/трек недоступен (диагностика — в лог).</summary>
    public async Task<ScApiStreamUrls?> GetStreamUrlsAsync(string scId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(scId) || !IsConnected) return null;

        var (status, body) = await SendAuthorizedAsync(
            $"{ApiBase}/tracks/{TrackUrn(scId)}/streams", forceRefresh: false, ct);
        if (status == 401)
        {
            // Access_token протух (живёт ~час): рефреш и один повтор.
            Logger.Info("SoundCloud official streams 401 — refreshing access token");
            (status, body) = await SendAuthorizedAsync(
                $"{ApiBase}/tracks/{TrackUrn(scId)}/streams", forceRefresh: true, ct);
        }
        if (status != 200)
        {
            // 403 с «disallowed» в теле — блокировка самого клиента API (все треки),
            // а не отказ в правах на конкретный трек: запоминаем для фолбэк-логики.
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
        // Диагностика «не играют»: если пришли только preview-стримы — аккаунт/приложение
        // не имеет прав на полную версию (Go+ / региональные ограничения), это не сбой сети.
        if (!urls.HasAny)
            Logger.Warn($"SoundCloud official streams: full URLs absent " +
                        $"(preview={preview != null}, scId={scId}) — no playback rights");
        return urls;
    }

    /// <summary>GET /tracks/{urn}/related — related-треки (рекомендации SC).
    /// Возвращает до limit треков, доступных для стриминга аккаунту.</summary>
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
                Title = title ?? "(без названия)",
                User = new ScUser { Username = artist ?? "" },
                DurationMs = durationMs ?? 0,
                ArtworkUrl = artwork,
                Streamable = streamable
            });
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>GET /me/likes/tracks — официальные лайки аккаунта (вместо парсинга
    /// неофициального эндпоинта, который периодически ломал JSON).</summary>
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
                Title = title ?? "(без названия)",
                User = new ScUser { Username = artist ?? "" },
                DurationMs = durationMs ?? 0,
                ArtworkUrl = artwork,
                Streamable = true
            });
            if (list.Count >= limit) break;
        }
        return list;
    }

    // ========================= Сетевые примитивы ====================

    private static bool IsSuccess(int status) => status is >= 200 and < 300;

    /// <summary>GET с Authorization: OAuth. 401 + forceRefresh — перед запросом обновить токен.
    /// Тело — строка; сетевые повторы (direct↔прокси) делает SoundCloudHttp.</summary>
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
