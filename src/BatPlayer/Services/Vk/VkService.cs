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

/// <summary>Ошибка VK-слоя после всех повторов. Code — код ошибки (0 — транспорт/парсинг, 5 — сессия).</summary>
public sealed class VkApiException : Exception
{
    public int ErrorCode { get; }

    public VkApiException(string message, int errorCode = 0) : base(message) => ErrorCode = errorCode;
}

/// <summary>
/// Клиент VK Music. Авторизация — COOKIES ВЕБ-СЕССИИ vk.com/vk.ru (тот же принцип, что у
/// SoundCloud-интеграции): окно входа собирает cookies из WebView2, они лежат в vk_auth.json,
/// а каталог берётся внутренним веб-эндпоинтом al_audio.php (act=load_section) — тем же,
/// которым пользуется веб-плеер VK.
///
/// Почему не OAuth: audio.get через сторонний токен мёртв — VK отдаёт коды 3/8 для всех
/// сторонних приложений (включая Kate Mobile), каталог по токену недоступен.
///
/// Скачивание файлов НЕ реализовано: только каталог (метаданные + временные ссылки),
/// стриминг через дисковый кэш (VkStreamCache) и матчинг с локальной библиотекой.
/// Сеть — прямой слой <see cref="VkHttp"/>: direct первым, системный прокси как фолбэк.
/// Cookies в логи не попадают — логируются только метод, коды ответов и число элементов.
/// </summary>
public sealed class VkService
{
    public const string AuthFileName = "vk_auth.json";

    /// <summary>
    /// Расчётный размер страницы пагинации load_section. VK отдаёт раздел «relevance» целиком
    /// одним ответом и offset поддерживает не всегда; константа нужна guard'у пагинации,
    /// когда в payload виден счётчик больше собранного.
    /// </summary>
    public const int PageSize = 1000;

    /// <summary>Защита от зацикливания пагинации load_section (10 × 1000 = 10k треков).</summary>
    public const int MaxAudioPages = 10;

    /// <summary>Сколько живёт разрешённая mp3-ссылка в памяти до повторной выборки каталога.</summary>
    private static readonly TimeSpan StreamUrlTtl = TimeSpan.FromMinutes(20);

    /// <summary>Минимальный интервал между полными обновлениями кэша ссылок (после неудачного резолва).</summary>
    private static readonly TimeSpan UrlRefreshCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Актуальный домен VK (веб-эндпоинт каталога).</summary>
    internal const string WebAudioUrlRu = "https://vk.ru/al_audio.php";

    /// <summary>Исторический домен — фолбэк, если .ru не ответил как al_audio.</summary>
    internal const string WebAudioUrlCom = "https://vk.com/al_audio.php";

    /// <summary>Referer, который ждёт веб-эндпоинт (страница аудио).</summary>
    internal const string WebAudioReferer = "https://vk.ru/audio";

    /// <summary>Обычная мобильная страница входа VK (окно входа открывает её первой).</summary>
    internal const string LoginUrl = "https://m.vk.com/login";

    /// <summary>UA для al_audio.php и CDN обложек; internal — проставляется фабрикой VkHttp.</summary>
    /// <remarks>
    /// Использован UA официального мобильного приложения VK для Android для обхода
    /// блокировки сторонних клиентов. VK проверяет UA и может отдавать голосовое
    /// сообщение вместо музыки при детектировании браузера.
    /// </remarks>
    internal const string UserAgent =
        "VKAndroidApp/7.52-14788 (Android 13; SDK 33; arm64-v8a; Samsung SM-G998B; ru; 2400x1080)";

    /// <summary>Параллелизм батч-закачки обложек (см. SyncArtworksAsync).</summary>
    private const int ArtworkDownloadParallelism = 4;

    private readonly VkAuthService _auth;
    private readonly VkArtworkCache _artworks = new();

    /// <summary>vk_id → временная mp3-ссылка (из последнего load_section). URL в БД не пишется.</summary>
    private readonly Dictionary<string, (string Url, DateTime FetchedUtc)> _urlCache = new(StringComparer.Ordinal);

    /// <summary>Замок кэша ссылок (заполняется из синка, читается из резолвера плеера).</summary>
    private readonly object _urlLock = new();

    /// <summary>Полная выборка каталога (обновление ссылок) не должна идти параллельно.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private DateTime _lastUrlRefreshUtc = DateTime.MinValue;

    /// <summary>Прогресс синхронизации: (обработано, всего-оценка).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    public VkService(string authFilePath) => _auth = new VkAuthService(authFilePath);

    /// <summary>Есть ли файл сессии (не проверяет его валидность).</summary>
    public bool HasAuthFile => _auth.Exists;

    /// <summary>
    /// Подключён ли аккаунт: файл сессии есть И в нём непустая cookie-строка.
    /// Сетевая проверка не выполняется — протухшая сессия выяснится при первом запросе.
    /// </summary>
    public bool HasWebSession => !string.IsNullOrWhiteSpace(_auth.Load().CookieHeader);

    /// <summary>id пользователя VK из файла сессии ("" — неизвестен); для статуса в настройках.</summary>
    public string GetUserId() => _auth.Load().UserId ?? string.Empty;

    // ============================ Сессия ============================

    /// <summary>
    /// Сохранить cookies веб-сессии после успешного входа (вызывается окном входа).
    /// Сами cookies никуда не логируются. AccessToken в файле не трогаем — поле
    /// оставлено для совместимости со старыми файлами.
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
    /// Пометить сессию недействительной (VK вернул страницу логина): cookie-строка
    /// очищается, UserId остаётся (пригодится при повторном входе). Файл не удаляется —
    /// кнопка Connect открывает окно входа заново.
    /// </summary>
    public void InvalidateSession()
    {
        var file = _auth.Load();
        file.CookieHeader = null;
        _auth.Save(file);
        Logger.Warn("VK web session invalidated — sign in again");
    }

    /// <summary>Отключение аккаунта: удалить vk_auth.json и кэш ссылок. Таблицу чистит вызывающий код.</summary>
    public void Disconnect()
    {
        _auth.Delete();
        lock (_urlLock) _urlCache.Clear();
    }

    /// <summary>Дата последней успешной синхронизации (из vk_auth.json), null — ещё не синхронизировали.</summary>
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

    // ======================= Синхронизация =========================

    /// <summary>
    /// Синхронизация музыки: выборка каталога веб-эндпоинтом al_audio.php
    /// (act=load_section) с cookies веб-сессии. Возвращает число сохранённых треков.
    /// Бросает <see cref="VkApiException"/> (код 5 — сессия протухла) — VM показывает
    /// понятное сообщение.
    /// </summary>
    public async Task<int> SyncAudioAsync(VkTracksRepository repository, CancellationToken ct)
    {
        var rows = await FetchAudioWebAsync(repository, ct);
        return rows.Count;
    }

    /// <summary>
    /// Полная выборка каталога (load_section по страницам guard'а). С репозиторием —
    /// сохраняет метаданные, пишет дату синка и докачивает обложки; без репозитория
    /// (обновление ссылок) — только наполняет кэш mp3-ссылок. В обоих случаях обновляет
    /// _urlCache и _lastUrlRefreshUtc.
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
                // Челлендж авторизации аудио: сессия сбрасывается — повторный вход
                // через окно (с прогревом vk.ru/audio) восстановит доступ.
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

            // Пагинация: секции отдают по 50; пока страница полная — запрашиваем дальше.
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

    /// <summary>Страница секции «recent» отдаёт ровно столько кортежей.</summary>
    private const int MaxSectionSize = 50;

    /// <summary>Строки каталога из кортежей секции recent.</summary>
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
    /// Следующий offset или null — страница была последней. Явный nextOffset из payload
    /// (если VK его прислал и он сдвигает вперёд) приоритетнее расчёта по total; расчёт
    /// следующего offset — та же чистая функция <see cref="NextAudioOffset"/>. Внутренний — покрыт юнит-тестами.
    /// </summary>
    internal static int? NextWebOffset(int currentOffset, AlAudioPayload payload)
    {
        if (payload.NextOffset is int explicitNext && explicitNext > currentOffset)
            return explicitNext;

        return NextAudioOffset(currentOffset, payload.Tracks.Count, payload.ReportedTotal, PageSize);
    }

    /// <summary>
    /// Следующий offset пагинации load_section или null — страница была последней.
    /// Чистая функция (покрыта юнит-тестами):
    ///   • пустая страница — конец;
    ///   • короткая страница (меньше запрашиваемого размера) — конец;
    ///   • накопленное число элементов достигло заявленного total — конец.
    /// </summary>
    internal static int? NextAudioOffset(int currentOffset, int fetchedItems, int reportedTotal, int pageSize)
    {
        if (fetchedItems <= 0) return null;
        if (reportedTotal > 0 && currentOffset + fetchedItems >= reportedTotal) return null;
        if (fetchedItems < pageSize) return null;
        return currentOffset + fetchedItems;
    }

    /// <summary>
    /// Пакетная закачка обложек всех VK-треков в artworks_cache/vk_{vk_id}.jpg (см.
    /// <see cref="VkArtworkCache"/>): до <see cref="ArtworkDownloadParallelism"/> параллельных
    /// скачиваний, пропуск уже скачанных и треков без artwork_url. Ошибки одного файла —
    /// лог + пропуск. Результаты в БД пишутся последовательно (у репозитория одно соединение).
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
            // Обложки — вспомогательная часть синка: сетевой сбой не должен бить по метаданным.
            Logger.Error(ex, "VK artwork batch download failed");
        }
    }

    /// <summary>Префикс файлов обложек VK в общем каталоге artworks_cache (sc_id — цифры).</summary>
    internal const string ArtworkIdPrefix = "vk_";

    // ============================ Стрим ==============================

    /// <summary>
    /// Прямая mp3-ссылка трека для стриминга. Ссылки из load_section временные и в БД не
    /// сохраняются: держим их в памяти сессии (TTL), а если ссылка протухла/сессия новая —
    /// один раз перечитываем каталог и пробуем снова; последняя инстанция — точечный
    /// reload_audio по конкретному треку (каталог type=recent покрывает не весь список).
    /// null — ссылку получить не удалось (нет сессии, трек недоступен).
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
            // Полная выборка не удалась (сеть/челлендж) — пробуем точечный reload_audio.
            return await ResolveReloadAudioAsync(track, ct);
        }

        if (TryGetFreshUrl(track.VkId, out url)) return url;

        // Трека нет в свежих секциях каталога (импортирован давно, вне первых страниц)
        // или его хэш не декодируется — точечный reload_audio по конкретному id.
        return await ResolveReloadAudioAsync(track, ct);
    }

    /// <summary>Точки отказа reload_audio: повторная попытка не раньше TTL — недоступный
    /// трек не должен долбить VK на каждый клик.</summary>
    private static readonly TimeSpan ReloadFailureTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _reloadFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// Точечное разрешение ссылки одного трека: POST al_audio.php act=reload_audio
    /// ids={owner}_{id} — тот вызов, которым веб-плеер догружает URL при клике.
    /// Ответ — тот же позиционный формат; StreamUrl вытаскивает lenient-парсер.
    /// null — VK не дал ссылку (недоступен/удалён) или недавняя попытка уже провалилась.
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
            // Сессия сброшена/челлендж — резолвер не должен ронять воспроизведение.
            Logger.Error($"VK reload_audio failed for {track.VkId} (error code {ex.ErrorCode})");
            lock (_urlLock) _reloadFailures[track.VkId] = DateTime.UtcNow;
            return null;
        }
    }

    /// <summary>Сеть reload_audio + разбор. Внутренняя — тестируемая часть отдельно.</summary>
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
    /// URL из ответа reload_audio: точное совпадение vk_id; если разобрался ровно один
    /// трек — берём его (запрос был по одному id, раскладка id/owner_id могла сместиться).
    /// Ссылка audio_api_unavailable.mp3?extra=... декодируется тем же путём, что и хэши
    /// секции каталога; неиграбельные ответы дают null.
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
            // Декоду нужен viewer_id как число; нет id — ссылку не собрать.
            if (!long.TryParse(userId, out var vkUserId) || vkUserId == 0) return null;
            url = VkAudioUrlDecoder.Decode(url, vkUserId);
        }

        return string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.Ordinal) ? null : url;
    }

    /// <summary>Обновление кэша ссылок полной выборкой каталога (не чаще cooldown'а).</summary>
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

    /// <summary>Запомнить временные ссылки страницы каталога (неиграбельные пропускаем).</summary>
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
                
                // reload_audio API не реализован — строим unavailable.mp3 и декодируем
                var unavailableUrl = $"https://vk.ru/mp3/audio_api_unavailable.mp3?extra={tuple.UrlHash}";
                var streamUrl = VkAudioUrlDecoder.Decode(unavailableUrl, vkUserId);
                
                if (string.IsNullOrEmpty(streamUrl) || !streamUrl.StartsWith("http", StringComparison.Ordinal))
                    continue;
                
                var vkId = $"{tuple.OwnerId}_{tuple.AudioId}";
                _urlCache[vkId] = (streamUrl, now);
            }
        }
    }

    // ====================== Сетевые примитивы =======================

    /// <summary>
    /// POST al_audio.php: сначала vk.ru, при невнятном ответе — исторический vk.com.
    /// Ответ отдаётся как есть: разбор (в т.ч. детект страницы логина) — у вызывающего.
    /// Cookies идут заголовком и в логи не пишутся.
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
    /// Один POST load_section. Тело формы — как у веб-плеера VK:
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
            // Cookie-строка сессии — единственная авторизация запроса (в лог не попадает).
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "com.vkontakte.android");
            request.Headers.TryAddWithoutValidation("Referer", WebAudioReferer);
            // Дополнительные заголовки для имитации мобильного приложения VK
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            return request;
        }, ct);
    }

    /// <summary>
    /// POST al_audio.php act=reload_audio — точечный догруз URL конкретного трека
    /// (тот же вызов, что делает веб-плеер при клике). Форма: act=reload_audio&amp;al=1&amp;ids=…
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

    /// <summary>Код ошибки означает отсутствие доступа к аудио (сообщение — VkAudioPermissionDenied).</summary>
    public static bool IsAudioPermissionError(int errorCode) => errorCode is 15 or 26 or 201;

    // ==================== Разбор ответов (тестируемое) ==============

    /// <summary>
    /// Разобранные треки → строки БД. Скрытые/удалённые записи (без url — IsPlayable=false)
    /// в каталог не пишутся. duration в payload — секунды; vk_id — "{owner_id}_{id}".
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

    /// <summary>vk_id = "{owner_id}_{id}" (id трека уникален только в паре с владельцем).</summary>
    internal static string BuildVkId(long ownerId, long id) => $"{ownerId}_{id}";

    // ===================== userId веб-сессии ========================

    private static readonly System.Text.RegularExpressions.Regex[] UserIdPatterns =
    [
        // Порядок приоритета: встроенные конфиги страницы содержат id зрителя,
        // ссылки на профили — последняя надежда (могут вести на другого пользователя).
        new("\"uid\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("\"viewer_id\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("viewer_id=(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("\"user_id\":(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled),
        new("href=\"/id(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled)
    ];

    /// <summary>
    /// id пользователя из HTML-страницы VK (feed/boot-данные/профильные ссылки) или null.
    /// Чистая функция — покрыта юнит-тестами. Окно входа вызывает её для HTML, снятого
    /// с WebView2 (ExecuteScriptAsync) и скачанного HttpClient'ом с cookies сессии.
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
