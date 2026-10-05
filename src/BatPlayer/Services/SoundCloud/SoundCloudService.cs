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

namespace BatPlayer.Services.SoundCloud;

/// <summary>Ошибка API SoundCloud после всех повторов (401/403 с новым client_id, 5xx и т.п.).</summary>
public sealed class SoundCloudApiException : Exception
{
    public int StatusCode { get; }

    public SoundCloudApiException(string message, int statusCode) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// Клиент неофициального web-API SoundCloud (api-v2.soundcloud.com).
/// Авторизация — cookies веб-сессии (sc_auth.json), полученные через окно входа (WebView2).
/// Скачивание аудио НЕ реализовано: только метаданные, стриминг (прямая mp3-ссылка) и матчинг.
/// Сеть — общий статический слой SoundCloudHttp: direct с фолбэком на прокси (см. его доку).
/// Cookies/client_id в логи не попадают — в исключения и лог пишутся только коды ответов и URL без токенов.
/// </summary>
public sealed class SoundCloudService
{
    public const string AuthFileName = "sc_auth.json";
    private const string ApiBase = "https://api-v2.soundcloud.com";
    // mime AAC-транскодингов — "audio/mp4; codecs=\"mp4a.40.2\"": сравниваем по префиксу.
    private const string AacMimeType = "audio/mp4";
    private const int PageSize = 200;
    // Защита от зацикливания пейджинга при сбойном next_href (~50 страниц × 200 = 10k лайков).
    private const int MaxLikePages = 50;

    // Без UA SoundCloud отдаёт упрощённую страницу/403 для html-запросов.
    // internal: проставляется фабрикой SoundCloudHttp на общих HttpClient.
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions JsonOpts = new();

    // Параллелизм батч-закачки обложек (см. SyncArtworksAsync).
    private const int ArtworkDownloadParallelism = 4;

    private readonly SoundCloudAuthStore _auth;
    private readonly SoundCloudClientIdProvider _clientIds;
    private readonly SoundCloudArtworkCache _artworks = new();

    /// <summary>Скачивания обложек «по требованию»: дедупликация параллельных запросов
    /// одного трека (лоадер видимых карточек и батч-синк), без накопления завершённых задач.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _artworkDownloads = new();

    /// <summary>Путь кэш-файла обложки трека (карточки биндят именно его).</summary>
    public string GetArtworkCachePath(string scId) => _artworks.GetCacheFilePath(scId);

    /// <summary>Обложка в кэше по требованию (видимая карточка): скачать, если файла ещё нет.
    /// Параллельные запросы одного scId ждут одно скачивание. Путь или null (ошибка/нет URL).</summary>
    public Task<string?> EnsureArtworkPathAsync(string scId, string? artworkUrl, CancellationToken ct)
        => _artworkDownloads.GetOrAdd(scId, _ => Task.Run(() => DownloadArtworkAsyncCore(scId, artworkUrl)));

    private async Task<string?> DownloadArtworkAsyncCore(string scId, string? artworkUrl)
    {
        try
        {
            // Без внешнего ct: общий таск для нескольких просящих, отмена внешнего
            // звонящего не должна портить результат остальным.
            return await _artworks.EnsureDownloadedAsync(scId, artworkUrl, CancellationToken.None);
        }
        finally
        {
            // Завершённые (в т.ч. упавшие) задачи не храним: следующий запрос либо
            // найдёт файл в кэше, либо честно попробует скачать заново.
            _artworkDownloads.TryRemove(scId, out _);
        }
    }

    /// <summary>Прогресс синхронизации лайков: (обработано, всего-оценка).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    /// <summary>Официальное подключение (pairing-код): когда подключено, стримы трека
    /// резолвятся ТОЛЬКО через него — миксовать с неофициальным api-v2 не нужно.</summary>
    private readonly SoundCloudOfficialApi? _officialApi;

    /// <summary>Подключён ли официальный SoundCloud API (OAuth-токен pairing-подключения).</summary>
    public bool OfficialApiConnected => _officialApi?.IsConnected == true;

    /// <summary>Официальный API живой: подключён И не отдал 403 «disallowed» (блокировка
    /// клиента целиком — тогда от него не будет ни одного стрима, и фолбэки не тормозим).</summary>
    public bool OfficialApiUsable => OfficialApiConnected && _officialApi?.IsDisallowed != true;

    // ===== Предохранитель веб-сессии =====
    // api-v2 ответил 401 после рефреша client_id: oauth_token из cookies истёк.
    // Для обычных треков это невидимо (media-резолв работает анонимно), но
    // MONETIZE-треки (AD_SUPPORTED) отдают на media-эндпоинты 404 без живой
    // сессии — без диагностики они выглядят как «DRM/недоступен».
    private volatile bool _webSessionExpired;

    /// <summary>Веб-сессия (cookies) истекла: последний /me (или другой авторизованный
    /// api-v2 запрос) ответил 401. Сбрасывается успешным /me и сохранением новых cookies.</summary>
    public bool IsWebSessionExpired => _webSessionExpired;

    /// <summary>Cookies сессии сохранены заново (вход/переподключение аккаунта):
    /// слушатели сбрасывают кэши, зависящие от сессии (чёрный список провалов резолва).</summary>
    public event EventHandler? SessionChanged;

    private void MarkWebSessionExpired()
    {
        if (_webSessionExpired) return;
        _webSessionExpired = true;
        Logger.Warn("SoundCloud web session expired (api-v2 401) — reconnect the account in Settings; " +
                    "MONETIZE (ad-supported) tracks need an authenticated session");
    }

    /// <summary>
    /// Жива ли веб-сессия: GET /me. true — жива; false — истекла (401 после рефреша
    /// client_id); null — неопределённо (сети нет / прочий код) — диагноз не ставим,
    /// чтобы сетевой сбой не выглядел как «переподключитесь».
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
            return null; // сетевой сбой — не ставим диагноз «сессия истекла»
        }
    }

    public SoundCloudService(string authFilePath, SettingsService? settings = null,
        SoundCloudOfficialApi? officialApi = null)
    {
        _officialApi = officialApi;
        _auth = new SoundCloudAuthStore(authFilePath);

        // Единый сетевой слой (direct/прокси/антиблокировка) для нас и SoundCloudClientIdProvider;
        // на смене настроек — перечитываем SoundCloudProxy и пересоздаём выбор транспорта.
        SoundCloudHttp.ZapretEnabled = settings?.Current.SoundCloudZapretEnabled ?? true;
        SoundCloudHttp.ApplyUserSetting(settings?.Current.SoundCloudProxy);
        // event нельзя комбинировать с ?. (CS0070: чтение события извне объявляющего типа).
        if (settings != null)
            settings.SettingsChanged += (_, _) =>
            {
                SoundCloudHttp.ZapretEnabled = settings.Current.SoundCloudZapretEnabled;
                SoundCloudHttp.ApplyUserSetting(settings.Current.SoundCloudProxy);
            };

        _clientIds = new SoundCloudClientIdProvider(_auth);
    }

    /// <summary>Есть ли файл веб-сессии (не проверяет её валидность).</summary>
    public bool HasAuthFile => _auth.Exists;

    // ============================ Сессия ============================

    /// <summary>
    /// Проверка сессии: GET /me. 200 → пользователь; иначе null (не подключено / cookies протухли).
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

    // ========================= Синхронизация ========================

    /// <summary>
    /// Синхронизация лайков: GET /users/{userId}/likes (limit=200), пагинация по next_href —
    /// это полный URL с курсором по времени лайка (НЕ offset-число), следуем до его отсутствия.
    /// Элементы collection[].playlist пропускаются (в DTO они приходят с Track == null).
    /// UserId берётся из кэша sc_auth.json или резолвится через /me (см. GetUserIdAsync).
    /// Возвращает число сохранённых треков. Бросает SoundCloudApiException — VM показывает ошибку.
    /// </summary>
    public async Task<int> SyncLikesAsync(SoundCloudLikesRepository repository, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var syncedAt = DateTime.UtcNow.ToString("o");
        int done = 0;

        // Первый запрос — по id пользователя; дальше следуем next_href из ответа.
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
            // total неизвестен до конца пейджинга: оценка done + страница, если есть продолжение.
            SyncProgress?.Invoke(this, (done, done + (response.NextHref != null ? PageSize : 0)));

            // client_id в next_href уже есть; AppendClientId идемпотентен.
            nextUrl = string.IsNullOrEmpty(response.NextHref) ? null : response.NextHref;
            if (response.Collection.Count == 0) break; // страховка от сбойного ответа без элементов
        }

        if (nextUrl != null)
            Logger.Warn($"SoundCloud likes pagination stopped at page cap ({MaxLikePages} pages)");

        SetLastSyncedUtc(DateTime.UtcNow);

        // Обложки — отдельный батч ПОСЛЕ основного апсерта лайков: сами лайки уже сохранены,
        // сбой обложек завершённый синк не роняет. Paths пишутся в artwork_local_path,
        // карточки биндят локальный файл (i1.sndcdn.com напрямую недоступен).
        await SyncArtworksAsync(repository, ct);

        return done;
    }

    /// <summary>
    /// Пакетная закачка обложек всех лайков в artworks_cache/{scId}.jpg (см. SoundCloudArtworkCache):
    /// до <see cref="ArtworkDownloadParallelism"/> параллельных скачиваний, пропуск уже скачанных
    /// и лайков без artwork_url. Ошибки одного файла — лог + пропуск. Результаты в БД
    /// (artwork_local_path) пишутся последовательно — у репозитория одно SqliteConnection.
    /// Покрывает и бэкфилл: лайки старых синков без локальной обложки докачиваются.
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
                    // Через общий дедуп: карточка, видимая сейчас, могла уже начать
                    // скачивание этой же обложки — параллельные записи в один .part
                    // не допускаем.
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
            // Обложки — вспомогательная часть синка: сетевой сбой не должен бить по лайкам.
            Logger.Error(ex, "SoundCloud artwork batch download failed");
        }
    }

    /// <summary>
    /// UserId подключённого пользователя: из кэша sc_auth.json (поле UserId); если пусто —
    /// GET /me, id сохраняется в auth-файл для следующих синхронизаций.
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

    /// <summary>Дата последней успешной синхронизации (из sc_auth.json), null — ещё не синхронизировали.</summary>
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

    // ============================ Стрим ==============================

    /// <summary>
    /// Полная информация о треке по id (нужно для карточек, загруженных из БД без transcodings).
    /// </summary>
    public async Task<ScTrack?> GetTrackAsync(string scId, CancellationToken ct)
    {
        var (status, json) = await SendApiAsync($"{ApiBase}/tracks/{Uri.EscapeDataString(scId)}",
            forceRefreshClientId: false, ct);
        return status == 200 ? ParseTrackJson(json) : null;
    }

    /// <summary>
    /// Похожие треки SoundCloud (GET /tracks/{id}/related) — «сцена» трека по графу
    /// SoundCloud, пул E «Моей волны». null — сеть/ошибка/не-200 (рефреш client_id
    /// при 401/403 внутри SendApiAsync). Сетевой сбой не роняет генерацию волны.
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
    /// Разрешает прямую mp3-ссылку для стриминга: progressive transcoding →
    /// GET {transcoding.url}?client_id=... → JSON {"url": "..."}.
    /// null — progressive нет или CDN ответил не-200: используйте DownloadHlsMp3Async.
    /// </summary>
    public async Task<string?> GetPlayableStreamAsync(ScTrack track, CancellationToken ct)
    {
        var transcodingUrl = PickProgressiveUrl(track);
        if (transcodingUrl == null) return null;

        return await ResolveTranscodingAsync(transcodingUrl, ct);
    }

    /// <summary>GET {transcoding.url}?client_id → JSON {"url": "..."}. 401/403/404 — один
    /// рефреш client_id и повтор (протухший id у media-эндпоинта отдаёт и 401, и 404);
    /// не-200 → null (лог).</summary>
    private async Task<string?> ResolveTranscodingAsync(string transcodingUrl, CancellationToken ct)
    {
        var clientId = await _clientIds.GetClientIdAsync(forceRefresh: false, ct);
        var url = AppendClientId(transcodingUrl, clientId);
        var (status, json) = await SendWithAuthAsync(url, ct);
        if (status == 401 || status == 403 || status == 404)
        {
            // client_id мог протухнуть: один раз обновляем и повторяем — но только
            // если id реально сменился; запрос с тем же id всегда даст тот же ответ.
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

    /// <summary>Официальный SoundCloud API: GET /tracks/{urn}/streams (OAuth-токен
    /// pairing-подключения, рефреш при 401 — в SoundCloudOfficialApi) → официальный
    /// AAC HLS-плейлист — работает для ВСЕГО, что доступно аккаунту (вкл. Go+), и не
    /// зависит от неофициального api-v2 (который периодически отвечает 404 на
    /// медиа-эндпоинты). Склейка и патч длительности — та же механика, что у
    /// неофициального AAC-пути.
    /// expectedDurationMs — длительность трека из метаданных: если официальный стрим
    /// заметно короче, это 30-секундный сниппет (нет прав на полную версию) — не играем
    /// отрывок под полными метаданными.
    /// null — официальное подключение отсутствует / стримов нет / сбой сети.</summary>
    public async Task<byte[]?> DownloadOfficialAacAsync(string scId, long expectedDurationMs, CancellationToken ct)
    {
        if (_officialApi == null || string.IsNullOrEmpty(scId)) return null;
        // API заблокирован для клиента целиком: запрос на каждый трек только тормозит
        // резолв (то же 403 гарантирован) — сразу к неофициальному каскаду. Раз в
        // полчаса пробный запрос всё же уходит (ShouldSkipStreamsProbe) — на случай
        // разблокировки.
        if (_officialApi.ShouldSkipStreamsProbe) return null;

        var urls = await _officialApi.GetStreamUrlsAsync(scId, ct);
        if (urls == null) return null;

        // 1) Официальный AAC (hls_aac_160 — лучший вариант).
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

        // 2) MP3-фолбэк: официальный HLS mp3-плейлист, сегменты self-contained mp3.
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

    /// <summary>Стрим официального API заметно короче метаданных → это preview-сниппет
    /// (прав на полную версию нет). Играем только полные версии: отрывок под полными
    /// метаданными карточки пользователь воспринимает как «звук не тот».</summary>
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
        // Токен на каждый запрос: здесь могли обновить его при 401 на /streams.
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

    /// <summary>Официальный AAC-плейлист: fetch (2 уровня вложенности) + init-сегмент +
    /// параллельные сегменты + патч длительности fMP4. Сниппет (см. IsSnippet) → null.
    /// null — сбой на любом шаге.</summary>
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
    /// HLS-фолбэк: SC перевёл выдачу на HLS — progressive-транскодинг у части треков
    /// отсутствует или его CDN-резолв отдаёт HTTP 404. Берём hls-вариант с mp3-сегментами
    /// (mime audio/mpeg), качаем плейлист и склеиваем сегменты в один mp3 (сегменты
    /// self-contained, склейка валидна). null — HLS нет / скачать не удалось.
    /// </summary>    /// <summary>
    /// HLS-фолбэк: SC перевёл выдачу на HLS — progressive-транскодинг у части треков
    /// отсутствует или его CDN-резолв отдаёт HTTP 404. Берём hls-вариант с mp3-сегментами
    /// (mime audio/mpeg), качаем плейлист и склеиваем сегменты в один mp3 (сегменты
    /// self-contained, склейка валидна). null — HLS нет / скачать не удалось.
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

        // Сегменты качаем с ограниченным параллелизмом, собираем строго по порядку:
        // один битый сегмент рушит склейку — отдаём null (трек останется неиграбельным).
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

    /// <summary>Параллелизм скачивания HLS-сегментов.</summary>
    private const int HlsSegmentParallelism = 4;

    /// <summary>
    /// URL-ы сегментов плейлиста:_media-плейлист отдаёт их сразу; мастер
    /// (#EXT-X-STREAM-INF) — один уровень вложенности (берём первый вариант).
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
                return urls; // media-плейлист

            playlistUrl = urls[0]; // мастер-плейлист: спускаемся к media
        }

        return new List<string>();
    }

    /// <summary>Строки-сегменты m3u8: не-комментарии, относительные резолвятся по базе.</summary>
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
                // битая строка — пропускаем
            }
        }
        return result;
    }

    /// <summary>
    /// Скачивание сегмента с ПОВТОРАМИ: DPI-блокировки дропают соединения
    /// вероятностно (часть запросов проходит), поэтому оборванный/зависший сегмент
    /// перезапрашивается до N раз — загрузка в итоге доходит целиком, а не умирает
    /// от одного обрыва. Прогресс между попытками не сохраняется: сегмент мал
    /// (~100-300 КБ), дешевле перекачать.
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
                        // Невалидный ответ (404/403) — повторять бессмысленно: сегмента нет.
                        return null;
                    }
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) when (attempt < SegmentRetryAttempts && !ct.IsCancellationRequested)
            {
                // Обрыв/зависание тела (stall-таймаут) или сетевой сбой — повтор.
                Logger.Info($"SoundCloud segment retry {attempt}/{SegmentRetryAttempts - 1} ({ex.InnerException?.Message ?? ex.Message})");
                await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Выбор HLS-транскодинга AAC (mime audio/mp4): предпочитаем quality "sq"
    /// (AAC 160 kbps — то, что играет веб-плеер SoundCloud), иначе первый попавшийся
    /// AAC ("lq" — 96 kbps). null — AAC-транскодинга нет (старые ответы API, opus/mp3 only).
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
    /// AAC-вариант трека — то же качество, что у веб-плеера (HLS audio/mp4, обычно 160 kbps
    /// против 128 у progressive mp3): качаем init-сегмент и сегменты плейлиста, склеиваем в один
    /// fMP4 и проставляем общую длительность (см. Fmp4DurationPatcher — без неё Media Foundation
    /// не знает TotalTime, таймлайн и перемотка не работают). null — AAC нет / скачать не удалось.
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

        // Init-сегмент обязателен (ftyp+moov: без него склейка не откроется ни одним ридером).
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

        // Сегменты качаем с ограниченным параллелизмом, собираем строго по порядку:
        // один битый сегмент рушит склейку — отдаём null (каскад уйдёт в mp3-варианты).
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

        // Длительность: сумма EXTINF точнее метаданных; если плейлист её не дал — track.DurationMs.
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
    /// Тело media-плейлиста HLS: мастер (#EXT-X-STREAM-INF) — один уровень вложенности
    /// (берём первый вариант), media-плейлист парсим на init-сегмент, сегменты и длительность.
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

    /// <summary>Разобранный media-плейлист AAC HLS.</summary>
    internal sealed record HlsAacPlaylist(string? InitUrl, List<string> Segments, double TotalSeconds);

    /// <summary>
    /// media-плейлист: EXT-X-MAP:URI — init-сегмент; EXTINF:<dur>, перед строкой-сегментом —
    /// длительность сегмента (суммируем в TotalSeconds); относительные URL резолвятся по базе.
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
                    catch (UriFormatException) { /* битый URI — играем без init (не откроется, отдаст null выше) */ }
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
                    // битая строка — пропускаем
                }
                pendingDuration = null;
            }
        }

        return new HlsAacPlaylist(init, segments, totalSeconds);
    }

    /// <summary>Значение в кавычках атрибута тега HLS (#EXT-X-MAP:URI="...").</summary>
    private static string? ExtractQuoted(string line)
    {
        var open = line.IndexOf('"');
        if (open < 0) return null;
        var close = line.IndexOf('"', open + 1);
        return close < 0 ? null : line[(open + 1)..close];
    }

    /// <summary>
    /// Выбор HLS-транскодинга: prefers mp3-сегменты (mime audio/mpeg — склейка валидна),
    /// иначе любой hls (opus-склейку плеер не откроет — вернётся null на сегментах).
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

    // ========================= Авторизация ==========================

    /// <summary>Сохранить cookies веб-сессии (вызывается окном входа после детекта oauth_token).</summary>
    public void SaveSessionCookies(string cookieHeader)
    {
        var file = _auth.Load();
        file.Cookies = cookieHeader;
        _auth.Save(file);
        // Новая сессия: предыдущий диагноз «истекла» больше не действует, а кэши
        // (чёрный список провалов резолва) должны пересчитаться — сигнал слушателям.
        _webSessionExpired = false;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Отключение аккаунта: удалить sc_auth.json. Таблицу лайков чистит вызывающий код.
    /// Архив аккаунтов (sc_accounts.json.accounts) НЕ трогается — «Сменить аккаунт» может
    /// вернуть сохранённую сессию без повторного входа.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        _webSessionExpired = false;
    }


    /// <summary>Cookie-строка из sc_auth.json (только для внутреннего использования и тестов).</summary>
    internal string? GetCookies() => _auth.Load().Cookies;

    // ====================== Сетевые примитивы =======================

    /// <summary>
    /// API-запрос с cookies + client_id. При 401/403 один раз обновляет client_id и повторяет.
    /// Возвращает (код, тело). Токены в логи не пишутся.
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
        // 401 после рефреша client_id — не «протухший id»: истёк oauth_token веб-сессии.
        if (status == 401)
            MarkWebSessionExpired();
        return (status, body);
    }

    private async Task<(int status, string body)> SendWithAuthAsync(string url, CancellationToken ct)
    {
        // cookies читаем на каждый запрос (как раньше); в лог они не пишутся.
        var cookies = _auth.Load().Cookies;
        var oauthToken = ExtractOAuthToken(cookies);
        return await SoundCloudHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookies))
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            // oauth_token из cookies дублируем заголовком Authorization — часть api-v2
            // эндпоинтов (в т.ч. /me и /users/{id}/likes) отвечают по нему надёжнее.
            if (oauthToken != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", oauthToken);
            return request;
        }, ct);
    }

    /// <summary>
    /// oauth_token из cookie-строки (пара "oauth_token=&lt;value&gt;"). null — не найден/пустой.
    /// Значение никуда не логируется — используется только для заголовка Authorization.
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

    /// <summary>Путь media-URL без query (client_id и прочие токены в лог не пишутся).</summary>
    internal static string MediaPath(string url)
    {
        var queryIndex = url.IndexOf('?');
        return queryIndex < 0 ? url : url[..queryIndex];
    }

    /// <summary>Есть ли у трека зашифрованные FairPlay-транскодинги (cbc/ctr-encrypted-hls,
    /// ключи skd://). Если при этом стандартные progressive/hls отвергнуты media-эндпоинтом —
    /// трек защищён DRM (SoundCloud переводит MONETIZE-треки на FairPlay) и не «оживёт».</summary>
    public static bool HasEncryptedTranscodings(ScTrack track)
        => track.Media?.Transcodings.Any(t =>
            t.Format?.Protocol?.Contains("encrypted", StringComparison.OrdinalIgnoreCase) == true) == true;

    // ==================== Играбельная копия DRM-трека ==================

    /// <summary>Слова в названии копии, после которых это не та версия трека
    /// (замедления/ускорения, ремиксы, инструменталы, каверы). Слово не применяется,
    /// если оно есть в названии оригинала (трек-ремикс ищет ремикс).</summary>
    internal static readonly string[] BadReuploadWords =
    {
        "slowed", "sped up", "speed up", "reverb", "nightcore", "8d",
        "remix", "bootleg", "edit", "instrumental", "инструментал",
        "live", "концерт", "concert", "cover", "кавер", "karaoke", "караоке",
        "reaction", "реакция", "type beat", "mashup", "extended mix", "acoustic", "loop"
    };

    /// <summary>
    /// Поиск играбельной копии трека на SoundCloud (перекачанной другими
    /// пользователями): DRM-оригиналы (AD_SUPPORTED) играют только через Widevine,
    /// а копии обычно залиты как обычные треки. GET /search/tracks по
    /// «исполнитель + название», фильтры: не оригинал, стримабельный, не SNIPPET/BLOCK,
    /// не AD_SUPPORTED, все слова названия на месте, нет live/remix-маркеров,
    /// длительность в пределах ±10 с, артист упомянут. Возвращает до limit кандидатов
    /// по возрастанию расхождения длительности (полные метаданные кандидата — через
    /// GetTrackAsync, там же финальная проверка). Пустой список — уверенных копий нет.
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

    /// <summary>Разбор выдачи /search/tracks и отбор уверенных копий (тестируемое).</summary>
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
        // Маркер «сам трек такой»: слово в названии оригинала не бракует кандидата.
        var scLow = (artist + " " + title).ToLowerInvariant();
        var badWords = BadReuploadWords.Where(w => scLow.Contains(w) == false).ToArray();

        foreach (var t in response.Collection)
        {
            if (t == null || t.Id == 0) continue;
            if (t.Id.ToString() == excludeScId) continue;                      // сам оригинал
            if (t.Streamable != true) continue;
            if (t.Policy is "SNIPPET" or "BLOCK") continue;                    // Go+/гео
            // Копия с той же рекламной моделью упрётся в тот же DRM — не кандидат.
            if (string.Equals(t.MonetizationModel, "AD_SUPPORTED", StringComparison.OrdinalIgnoreCase)) continue;

            var candidateTitle = t.Title ?? string.Empty;
            var low = candidateTitle.ToLowerInvariant();
            if (titleTokens.Count > 0 && titleTokens.Any(w => low.Contains(w) == false))
                continue;                                                      // название не то
            if (badWords.Any(w => low.Contains(w)))
                continue;                                                      // slowed/remix/…
            if (durationMs is { } target && t.DurationMs is { } candDur && Math.Abs(candDur - target) > 10_000)
                continue;                                                      // версия не та по длине

            if (artistTokens.Count > 0)
            {
                var hay = (low + " " + (t.User?.Username ?? "") + " " + (t.FullName ?? "")).ToLowerInvariant();
                if (artistTokens.Count(a => hay.Contains(a)) * 2 < artistTokens.Count)
                    continue;                                                  // артист не упомянут
            }

            // Ранг: ближе длительность + бонус популярности копии (массовые перекачки
            // надёжнее одиночных).
            var durDist = durationMs is { } tg && t.DurationMs is { } cd ? Math.Abs(cd - tg) : 0L;
            var score = durDist - Math.Log10(Math.Max(t.PlaybackCount ?? 0, 10)) * 1000;
            ranked.Add((t.Id, candidateTitle, score));
        }

        return ranked.OrderBy(x => x.Score).ToList();
    }

    /// <summary>Убирает скобочные хвосты: «jiggy (feat. xaviersobased)» → «jiggy» —
    /// feat/prod-части только мешают поиску.</summary>
    internal static string StripBrackets(string title)
    {
        var cleaned = System.Text.RegularExpressions.Regex
            .Replace(title ?? string.Empty, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]", " ")
            .Trim();
        return cleaned.Length > 0 ? cleaned : (title ?? string.Empty).Trim();
    }

    /// <summary>Значимые слова в нижнем регистре (буквы/цифры, длина ≥ 2).</summary>
    private static List<string> TokenizeWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        return System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-zа-яё0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToList();
    }

    // ==================== Разбор ответов (тестируемое) ==============

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

    /// <summary>collection → строки БД; playlist'ы и треки без id пропускаются.</summary>
    internal static List<SoundCloudLikeRow> ExtractLikeRows(ScLikesResponse response, string syncedAt)
    {
        var rows = new List<SoundCloudLikeRow>();
        foreach (var item in response.Collection)
        {
            var track = item.Track;
            if (track == null || track.Id == 0) continue; // playlist и мусор

            rows.Add(new SoundCloudLikeRow
            {
                ScId = track.Id.ToString(),
                Title = track.Title ?? string.Empty,
                // username предпочтительнее full_name (у профилей имя бывает пустым).
                Artist = !string.IsNullOrWhiteSpace(track.User?.Username)
                    ? track.User!.Username
                    : (!string.IsNullOrWhiteSpace(track.User?.FullName)
                        ? track.User!.FullName!
                        : (track.FullName ?? string.Empty)),
                DurationMs = track.DurationMs,
                ArtworkUrl = BuildArtworkUrl(track.ArtworkUrl) ?? string.Empty,
                PermalinkUrl = track.PermalinkUrl ?? string.Empty,
                Streamable = track.Streamable,
                // created_at у обёртки — дата ЛАЙКА; fallback на дату загрузки трека.
                LikedAt = item.CreatedAt ?? track.CreatedAt,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    /// <summary>Обложка в большом размере: '-large' → '-t500x500' (SoundCloud отдаёт large по умолчанию).</summary>
    internal static string? BuildArtworkUrl(string? artworkUrl)
    {
        if (string.IsNullOrEmpty(artworkUrl)) return artworkUrl;
        return artworkUrl.Contains("-large", StringComparison.Ordinal)
            ? artworkUrl.Replace("-large", "-t500x500")
            : artworkUrl;
    }

    /// <summary>
    /// Выбор progressive-транскодинга (mp3 одним файлом). Отсутствует у
    /// HLS-only треков — для них есть DownloadHlsMp3Async.
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
