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
/// Клиент Яндекс Музыки. Авторизация — ОФИЦИАЛЬНЫЙ OAuth-токен Яндекс ID (неявный поток
/// с client_id приложения Яндекс Музыки), тот же принцип, что у SoundCloud/VK-интеграций:
/// окно входа получает токен через WebView2, он лежит в ym_auth.json, а все запросы к
/// API-хосту api.music.yandex.net идут с заголовком "Authorization: OAuth &lt;token&gt;"
/// (cookies веб-сессии хост отвечает 401 — Session_id веб-сессии не подходят). Заголовки
/// веб-клиента (X-Yandex-Music-Client: web) просят веб-раскладку JSON — по образцу
/// мобильного клиента.
///
/// Все эндпоинты требуют авторизацию: без токена хост отвечает 401. Каталог — лайки
/// пользователя (users/{uid}/likes/tracks отдают ТОЛЬКО id, полные объекты добираются батчами
/// через tracks?track-ids), стрим — tracks/{id}/download-info (раскладка вариантов менялась —
/// ссылка собирается lenient-парсером YmJsonParser, см. его доксуммарку).
///
/// Скачивание файлов НЕ реализовано: только каталог (метаданные), стриминг через дисковый
/// кэш (YmStreamCache) и матчинг с локальной библиотекой. Токен в логи не попадает —
/// логируются только метод, коды ответов и число элементов.
/// </summary>
public sealed class YmService
{
    public const string AuthFileName = "ym_auth.json";

    /// <summary>API-хост Яндекс Музыки (веб-клиент).</summary>
    internal const string ApiBase = "https://api.music.yandex.net";

    /// <summary>Значение X-Yandex-Music-Client/Yandex-Music-Client (веб-раскладка JSON).</summary>
    internal const string MusicClientHeader = "web";

    /// <summary>UA для API и CDN (обложки/стримы); internal — проставляется фабрикой YmHttp.</summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>Размер батча tracks?track-ids (лимит API — по образцу мобильного клиента).</summary>
    public const int TrackIdBatchSize = 100;

    /// <summary>Сколько живёт разрешённая mp3-ссылка в памяти до повторного download-info.</summary>
    private static readonly TimeSpan StreamUrlTtl = TimeSpan.FromMinutes(60);

    /// <summary>Варианты запроса download-info: transports + битрейты по убыванию. Раскладка
    /// ответа и доступные битрейты зависят от тарифа — пробируем варианты, пока не придёт
    /// хотя бы один вычислимый URL (см. YmJsonParser.ParseDownloadInfo).</summary>
    internal static readonly string[] DownloadInfoQueries =
    [
        "transports=encode_info_websonic,pure_d&bitrate=320",
        "transports=encode_info_websonic,pure_d&bitrate=192",
        "transports=encode_info_websonic,pure_d&bitrate=128"
    ];

    /// <summary>Параллелизм батч-закачки обложек (см. SyncArtworksAsync).</summary>
    private const int ArtworkDownloadParallelism = 4;

    private readonly YmAuthService _auth;
    private readonly YmArtworkCache _artworks = new();

    /// <summary>ym_id → временная mp3-ссылка (из последнего download-info). URL в БД не пишется.</summary>
    private readonly Dictionary<string, (string Url, DateTime FetchedUtc)> _urlCache = new(StringComparer.Ordinal);

    /// <summary>Замок кэша ссылок (заполняется из резолвера, читается из резолвера плеера).</summary>
    private readonly object _urlLock = new();

    /// <summary>Прогресс синхронизации: (обработано, всего-оценка).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    public YmService(string authFilePath) => _auth = new YmAuthService(authFilePath);

    /// <summary>
    /// Подключён ли аккаунт: файл сессии есть И в нём непустой OAuth-токен.
    /// Сетевая проверка не выполняется — отозванный токен выяснится при первом запросе.
    /// </summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(_auth.Load().AccessToken);

    /// <summary>uid аккаунта из файла сессии ("" — неизвестен); для статуса в настройках.</summary>
    public string GetSavedUid() => _auth.Load().Uid ?? string.Empty;

    /// <summary>Отображаемое имя аккаунта из файла сессии ("" — неизвестно); для статуса в настройках.</summary>
    public string GetSavedDisplayName() => _auth.Load().DisplayName ?? string.Empty;

    // ============================ Сессия ============================

    /// <summary>
    /// Сохранить OAuth-токен после успешного входа (вызывается окном входа).
    /// Сам токен никуда не логируется. uid/displayName приходят из account/status
    /// (могут быть пустыми — синк тогда сам дозаполнит их через EnsureAccountInfoAsync).
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
    /// Пометить сессию недействительной (API вернул 401/403): OAuth-токен очищается,
    /// uid/displayName остаются (пригодятся при повторном входе). Файл не удаляется —
    /// кнопка Connect открывает окно входа заново.
    /// </summary>
    public void InvalidateSession()
    {
        var file = _auth.Load();
        file.AccessToken = null;
        _auth.Save(file);
        Logger.Warn("Yandex Music OAuth token invalidated — sign in again");
    }

    /// <summary>Отключение аккаунта: удалить ym_auth.json и кэш ссылок. Таблицу чистит вызывающий код.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        lock (_urlLock) _urlCache.Clear();
    }

    /// <summary>Дата последней успешной синхронизации (из ym_auth.json), null — ещё не синхронизировали.</summary>
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

    // ======================== Сетевые примитивы =====================

    /// <summary>GET относительно ApiBase с OAuth-заголовком (токен в лог не попадает).</summary>
    private async Task<(int Status, string Body)> GetAsync(string pathAndQuery, string accessToken, CancellationToken ct)
        => await SendAsync(HttpMethod.Get, pathAndQuery, accessToken, ct);

    /// <summary>Произвольный метод (POST/DELETE — лайки) с OAuth-заголовком.</summary>
    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string pathAndQuery, string accessToken, CancellationToken ct)
        => await YmHttp.SendWithFailoverAsync(() =>
        {
            var request = new HttpRequestMessage(method, ApiBase + pathAndQuery);
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.yandex.ru/");
            return request;
        }, ct);

    /// <summary>Тело JSON при успехе; не-2xx → YmApiException с кодом (401/403 — сессия).</summary>
    private static string EnsureSuccess(int status, string body, string what)
    {
        if (status == 200) return body;
        throw new YmApiException($"{what} failed with HTTP {status}", status);
    }

    // ========================= Аккаунт ==============================

    /// <summary>
    /// Аккаунт по сохранённому OAuth-токену (uid + отображаемое имя). null — токена нет
    /// или ответ не сошёлся (401/прочее): статус настройки показывает сохранённое при
    /// входе имя.
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
            // Сетевой сбой проверки аккаунта не должен ронять вызывающий код.
            Logger.Error(ex, "Yandex Music account status failed");
            return null;
        }
    }

    /// <summary>
    /// Аккаунт по произвольному OAuth-токену (окно входа вызывает ДО сохранения сессии,
    /// чтобы сразу записать uid/displayName в файл). null — ответ не сошёлся.
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
    /// Гарантировать сохранённый uid: берём его из СТАТУСА ТОКЕНА, а не из файла — файл
    /// мог остаться от другого аккаунта, и синк тогда честно читал лайки чужого/старого
    /// аккаунта (жалоба: «лайки обновляются только после повторного входа»). DisplayName
    /// обновляется заодно. При сетевом сбое статуса — сохранённый uid как фолбэк.
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

    // ======================== Синхронизация =========================

    /// <summary>
    /// Синхронизация лайков: likes/tracks (только id) → батчи tracks?track-ids (по
    /// <see cref="TrackIdBatchSize"/>) → upsert в БД → докачка обложек. Возвращает число
    /// сохранённых треков. Бросает <see cref="YmApiException"/> (401/403 — токен
    /// отозван, сервис сам сбрасывает сессию) — VM показывает понятное сообщение.
    /// </summary>
    public async Task<int> SyncLikedTracksAsync(YmTracksRepository repository, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var uid = await EnsureUidAsync(ct);

        // 1) Лайки — id + время лайка (см. ParseLikeEntries).
        var (likesStatus, likesBody) = await GetAsync($"/users/{uid}/likes/tracks", accessToken, ct);
        var likeEntries = YmJsonParser.ParseLikeEntries(EnsureSuccess(likesStatus, likesBody, "likes/tracks"));
        Logger.Info($"Yandex Music sync: uid={uid}, likes={likeEntries.Count}");

        var syncedAt = DateTime.UtcNow.ToString("o");
        var total = likeEntries.Count;
        var saved = 0;
        SyncProgress?.Invoke(this, (0, total));

        // Порядок каталога держит liked_at (время лайка из API): «свежие лайки сверху»
        // и у повторных синков тоже — новые лайки не теряются в конце списка из 400+.
        var likedAtById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in likeEntries)
            likedAtById.TryAdd(e.Id.Split(':')[0], e.LikedAt);

        // 2) Полные объекты — батчами по 100 id (порядок лайков сохраняется).
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

            // Прогресс считаем по обработанным id (API может не вернуть часть id).
            SyncProgress?.Invoke(this, (Math.Min(total, saved + Math.Max(0, chunk.Count - tracks.Count)), total));
        }

        // 2b) Снятые с лайка треки удаляются: страница отражает текущие лайки,
        //     а не копит историю всех когда-либо синхронизированных.
        var removed = await repository.DeleteNotInAsync(
            likeEntries.Select(e => e.Id.Split(':')[0]));

        SetLastSyncedUtc(DateTime.UtcNow);
        Logger.Info($"Yandex Music sync done: saved={saved}, removed={removed}");

        // 3) Обложки — отдельный батч ПОСЛЕ основного апсерта: сами метаданные уже
        //    сохранены, сбой обложек завершённый синк не роняет.
        await SyncArtworksAsync(repository, ct);

        return saved;
    }

    /// <summary>
    /// Лайк/дизлайк трека в АККАУНТЕ Яндекс Музыки: POST users/{uid}/likes/tracks/add
    /// (параметр trackId) и .../remove (параметр track-ids). Сердечко в плеере для
    /// YM-треков дергает это — после синка трек появляется на странице ЯМ и в
    /// «Фаворитах» (раньше сердечко молча писало в локальную БД по отрицательному
    /// runtime-id, строки не было — лайк терялся). true — API подтвердил. Голые
    /// POST/DELETE на /likes/tracks сервер отвечает 405 Method Not Allowed, а /add с
    /// параметром track-ids — 400 «trackId: Parameter value is not set».
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
    /// Каталог с лендинга (чарты/новые релизы, блоки types=track) — без записи в БД.
    /// Резерв для блоков «Charts/Новые релизы»: персональный каталог страницы — лайки.
    /// </summary>
    public async Task<List<YmTrackDto>> GetLandingTracksAsync(CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        var (status, body) = await GetAsync("/landing?types=track&lang=ru", accessToken, ct);
        return YmJsonParser.ParseLandingTracks(EnsureSuccess(status, body, "landing"));
    }

    /// <summary>
    /// Похожие треки Яндекс Музыки (GET /tracks/{ymId}/similar) — граф сходства,
    /// построенный рекомендательной системой Яндекса. Основа «Моей волны»: по сидам
    /// из библиотеки пользователя собираются кандидаты, которых у него ещё нет.
    /// Бросает <see cref="YmApiException"/> (401/403 — токен отозван).
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
    /// Поиск треков по строке «исполнитель + название» (GET /search?type=track) —
    /// сопоставление сидов из VK/локальной библиотеки/SC с ym_id, чтобы их можно
    /// было подать в similar-эндпоинт. Возвращает до limit результатов (API сам
    /// ограничивает выдачу, лишнее отсекается здесь). Бросает YmApiException.
    /// </summary>
    public async Task<List<YmTrackDto>> SearchTracksAsync(string text, int limit, CancellationToken ct)
    {
        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new YmApiException("not connected", 401);

        // Параметр page ОБЯЗАТЕЛЕН (без него API отвечает 400 "Parameters requirements
        // are not met: [page: Parameter value is not set]") — проверено пробой 2026-09.
        var query = "/search?text=" + Uri.EscapeDataString(text)
                    + "&type=track&lang=ru&page=0";
        var (status, body) = await GetAsync(query, accessToken, ct);
        var tracks = YmJsonParser.ParseSearchTracks(EnsureSuccess(status, body, "search"));
        return limit > 0 && tracks.Count > limit ? tracks.Take(limit).ToList() : tracks;
    }

    /// <summary>
    /// Поиск исполнителя по имени (GET /search?type=artist) — разрешение имён из
    /// play_log в ym-artist-id для /artists/{id}/… (кэш сопоставлений в wave_seed_map).
    /// Бросает YmApiException.
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
    /// Треки исполнителя (GET /artists/{id}/tracks?page=…&amp;pageSize=limit) — каталог
    /// артиста безотносительно библиотеки пользователя: «треки исполнителей, которых я
    /// слушаю, но ещё не добавлены». Каталог берётся страницами: чем глубже, тем
    /// больше материала для ротации между миксами. Бросает YmApiException.
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
    /// Похожие исполнители (GET /artists/{id}/similar) — до 50 артистов той же сцены
    /// по графу Яндекса. Бросает YmApiException.
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
    /// Аудитория исполнителя (GET /artists/{id}/brief-info → result.stats.listeners) —
    /// фильтр ноунеймов и «нейро-треков» в доборе новизны волны. null — API не отдал
    /// поле. Бросает YmApiException.
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
    /// Радио-лента по исполнителю (GET /rotor/station/artist:{id}/tracks) — батч
    /// (~5 треков) «похожего звука» из ротора Яндекса. Бросает YmApiException.
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
    /// Обложка трека в локальном кэше artworks_cache/ym_{ym_id}.jpg (уже скачана — путь
    /// без сети; нет — скачивание по coverUri). null — нет URL или скачивание не удалось.
    /// Волна и страница Яндекс Музыки кэшируют обложки в общий каталог.
    /// </summary>
    public async Task<string?> EnsureArtworkAsync(string ymId, string? coverUri, CancellationToken ct)
        => await _artworks.EnsureDownloadedAsync(
            ArtworkIdPrefix + ymId, YmJsonParser.BuildArtworkUrl(coverUri), ct);

    /// <summary>Разобранные треки API → строки БД (чистая функция — покрыта юнит-тестами).</summary>
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
    /// Пакетная закачка обложек всех YM-треков в artworks_cache/ym_{ym_id}.jpg (см.
    /// <see cref="YmArtworkCache"/>): до <see cref="ArtworkDownloadParallelism"/> параллельных
    /// скачиваний, пропуск уже скачанных и треков без artwork_url. Ошибки одного файла —
    /// лог + пропуск. Результаты в БД пишутся последовательно (у репозитория одно соединение).
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
            // Обложки — вспомогательная часть синка: сетевой сбой не должен бить по метаданным.
            Logger.Error(ex, "Yandex Music artwork batch download failed");
        }
    }

    /// <summary>Префикс файлов обложек YM в общем каталоге artworks_cache (ym_id — цифры).</summary>
    internal const string ArtworkIdPrefix = "ym_";

    // ===================== OAuth Device Flow (вход) ==================

    /// <summary>Официальные OAuth-креды приложения Яндекс Музыки (Android-клиент,
    /// публичные, не секрет — как в неофициальном yandex-music-api). Именно эта пара
    /// заведена на oauth.yandex.ru: implicit-редирект с другим публично гуляющим
    /// client_id даёт 400 «Неизвестно приложение с таким client_id», поэтому вход
    /// идёт через Device Flow — код подтверждения на oauth.yandex.ru/device.</summary>
    internal const string OAuthClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    internal const string OAuthClientSecret = "53bc75238f0c4d08a118e51fe9203300";

    /// <summary>Эндпоинты Яндекс ID для Device Flow (отдельный хост, не ApiBase).</summary>
    internal const string OAuthDeviceCodeUrl = "https://oauth.yandex.ru/device/code";
    internal const string OAuthTokenUrl = "https://oauth.yandex.ru/token";

    /// <summary>Страница подтверждения кода, если Яндекс не прислал verification_url.</summary>
    internal const string DefaultVerificationUrl = "https://oauth.yandex.ru/device";

    /// <summary>Дефолты параметров Device Flow, если ответ не содержал expires_in/interval.</summary>
    internal const int DefaultExpiresInSeconds = 300;
    internal const int DefaultPollIntervalSeconds = 5;

    /// <summary>device_name, под которым токен появится в списке устройств Яндекс ID.</summary>
    internal const string DeviceName = "BatPlayer";

    /// <summary>
    /// Запрос кода устройства (шаг 1 Device Flow): пользователь вводит UserCode на
    /// VerificationUrl, плеер опрашивает токен через <see cref="PollDeviceTokenAsync"/>.
    /// Бросает <see cref="YmApiException"/> при сетевом сбое/не-200.
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
    /// Однократный опрос токена (шаг 2 Device Flow). Результаты разбирает вызывающий код:
    /// AccessToken — успех; IsPending — ждать следующего тика; IsSlowDown — увеличить
    /// интервал; IsExpired/IsDenied — прекратить опрос. Токен в лог не попадает.
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
        // 200 без access_token и 400 без error — раскладка ответа изменилась; статус
        // сохраняем для лога вызывающего кода.
        if (status != 200 && result.ErrorCode == null)
            result = new YmTokenResult { ErrorCode = "invalid_response" };
        return result;
    }

    /// <summary>Случайный device_id (10 символов латиница/цифры — как в yandex-music-api):
    /// токен появится в списке устройств аккаунта под этим идентификатором.</summary>
    internal static string GenerateDeviceId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    // ============================ Стрим ==============================

    /// <summary>
    /// Прямая mp3-ссылка трека для стриминга: кэш сессии (TTL) → GET tracks/{id}/download-info
    /// (сначала без параметров — раскладка 2025+, затем варианты transports/bitrate по
    /// убыванию). Современный ответ отдаёт downloadInfoUrl — XML-дескриптор, из которого
    /// собирается финальная ссылка https://{host}/get-mp3/{s}/{ts}{path}. Ссылки временные
    /// и в БД не сохраняются. null — ссылку получить не удалось (нет токена, трек
    /// недоступен); 401/403 сбрасывают сессию и бросают <see cref="YmApiException"/>.
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(string ymId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ymId)) return null;

        if (TryGetFreshUrl(ymId, out var url)) return url;

        var accessToken = _auth.Load().AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        // Раскладки запроса: сначала современный (без параметров), затем легаси-варианты.
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
                // Сеть недоступна — остальные битрейты не помогут.
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
                // Раньше не-200 проглатывался молча — теперь видно, что резолв не прошёл.
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
    /// Фетч XML-дескриптора по downloadInfoUrl и сборка финальной mp3-ссылки.
    /// null — дескриптор не получен/не распарсен (лог + пробуем следующий вариант).
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

    /// <summary>Лучший вариант ссылки: mp3 с максимальным битрейтом; иначе любой непустой.
    /// Вариант годится и с готовым Url, и с DownloadInfoUrl (дескриптор резолвится сервисом) —
    /// в современной раскладке download-info поле Url пустое, есть только дескриптор.</summary>
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

    /// <summary>Запомнить временную ссылку трека (TTL в <see cref="StreamUrlTtl"/>).</summary>
    private void CacheStreamUrl(string ymId, string url)
    {
        lock (_urlLock) _urlCache[ymId] = (url, DateTime.UtcNow);
    }

    // ========================== Утилиты ==============================

    internal static List<List<string>> Chunk(IReadOnlyList<string> ids, int size)
    {
        var chunks = new List<List<string>>();
        for (var i = 0; i < ids.Count; i += size)
            chunks.Add(ids.Skip(i).Take(size).ToList());
        return chunks;
    }
}
