using System.Text.Json;

namespace BatPlayer.Services.YandexMusic;

/// <summary>Ошибка слоя Яндекс Музыки после всех повторов. HttpCode — код ответа (0 — транспорт/парсинг,
/// 401/403 — сессия (OAuth-токен) недействительна).</summary>
public sealed class YmApiException : Exception
{
    public int HttpCode { get; }

    public YmApiException(string message, int httpCode = 0) : base(message) => HttpCode = httpCode;

    /// <summary>Код означает отозванный/недействительный OAuth-токен.</summary>
    public static bool IsSessionError(int httpCode) => httpCode is 401 or 403;
}

/// <summary>Трек из JSON-ответов API (likes→tracks, landing). Часть полей может отсутствовать —
/// разбор lenient; надёжны id и title.</summary>
public sealed class YmTrackDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    /// <summary>Исполнители через ", " (до трёх имён, как в веб-плеере).</summary>
    public string Artist { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    /// <summary>Шаблон обложки с "%%" вместо размера (albums[].coverUri); null — нет обложки.</summary>
    public string? CoverUri { get; init; }
    /// <summary>Трек доступен на текущем тарифе (поле available; отсутствие поля — считаем доступным).</summary>
    public bool Available { get; init; } = true;
}

/// <summary>Исполнитель из JSON-ответов API (поиск, похожие исполнители). Надёжен id.</summary>
public sealed class YmArtistDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>Данные аккаунта из account/status (uid + отображаемое имя).</summary>
public sealed class YmAccountInfo
{
    public string Uid { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Готовый вариант прямой mp3-ссылки из download-info (URL уже собран парсером).</summary>
public sealed class YmDownloadOption
{
    public string Codec { get; init; } = string.Empty;
    public int Bitrate { get; init; }
    public string Url { get; init; } = string.Empty;

    /// <summary>Ссылка на XML-дескриптор (storage.mds.yandex.net/…/download-info), из
    /// которого собирается финальная mp3-ссылка — раскладка современного API. Пустая —
    /// вариант уже содержит готовый Url (легаси-раскладки).</summary>
    public string DownloadInfoUrl { get; init; } = string.Empty;
}

/// <summary>Поля XML-дескриптора download-info: host/path/ts/s → финальная ссылка
/// https://{host}/get-mp3/{s}/{ts}{path}.</summary>
public sealed class YmDownloadInfoXml
{
    public string Host { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Ts { get; init; } = string.Empty;
    public string S { get; init; } = string.Empty;
}

/// <summary>Код устройства из POST oauth.yandex.ru/device-code (OAuth Device Flow).</summary>
public sealed class YmDeviceCode
{
    public string DeviceCode { get; init; } = string.Empty;
    public string UserCode { get; init; } = string.Empty;
    /// <summary>Страница подтверждения (oauth.yandex.ru/device); пустая — берётся дефолт.</summary>
    public string VerificationUrl { get; init; } = string.Empty;
    /// <summary>Через сколько секунд код истекает (0 — берётся дефолт).</summary>
    public int ExpiresInSeconds { get; init; }
    /// <summary>Рекомендованный интервал опроса в секундах (0 — берётся дефолт).</summary>
    public int IntervalSeconds { get; init; }
}

/// <summary>Ответ POST oauth.yandex.ru/token: либо токен, либо код OAuth-ошибки
/// (authorization_pending / slow_down / expired_token / access_denied / …).</summary>
public sealed class YmTokenResult
{
    public string AccessToken { get; init; } = string.Empty;
    public int? ExpiresInSeconds { get; init; }
    /// <summary>Код OAuth-ошибки из тела 400-ответа; null — ошибки нет (токен получен).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Пользователь ещё не подтвердил код — опрос продолжается.</summary>
    public bool IsPending => string.Equals(ErrorCode, "authorization_pending", StringComparison.Ordinal);

    /// <summary>Яндекс попросил опрашивать реже (poll_interval+5 c).</summary>
    public bool IsSlowDown => string.Equals(ErrorCode, "slow_down", StringComparison.Ordinal);

    /// <summary>Код истёк — нужен новый.</summary>
    public bool IsExpired => string.Equals(ErrorCode, "expired_token", StringComparison.Ordinal);

    /// <summary>Пользователь отказал в согласии / клиент отклонён — опрос смысла не имеет.</summary>
    public bool IsDenied => ErrorCode is "access_denied" or "invalid_client" or "unauthorized_client"
                            or "invalid_grant" or "bad_verification_code";
}

/// <summary>
/// Разбор JSON-ответов API Яндекс Музыки. Вынесен в отдельный статический класс: эндпоинты
/// api.music.yandex.net не документированы и меняются, поэтому весь парсинг — lenient
/// (мусор/неожиданная раскладка не бросают исключений, а дают пустой результат), изолирован
/// от сети и покрыт юнит-тестами на реалистичных фикстурах. Cookies/токены в ответах треков
/// не встречаются — логировать parsed-данные безопасно.
/// </summary>
public static class YmJsonParser
{
    // ========================= Треки (tracks?track-ids) =========================

    /// <summary>
    /// Ответ GET /tracks?track-ids=…&amp;lang=ru: {"result":[{…track…}, …]}.
    /// Порядок result соответствует порядку запрошенных id (недоступные id отсутствуют).
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

    // ========================= Лендинг (blocks) =========================

    /// <summary>
    /// Ответ GET /landing?types=track&amp;lang=ru: {"result":{"blocks":[{…, "tracks":[…]}]}}.
    /// Раскладка блоков меняется — треки собираются DFS-ом по всему дереву: трек-кандидат —
    /// объект с "id", строковым "title" и массивом "artists" или "albums"; дубликаты по id
    /// отбрасываются (трек может встретиться в нескольких блоках).
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
                // Кандидат в треки: объект с id+title И хотя бы одной платформенной
                // коллекцией (artists/albums). Без этого фильтра под DFS попадали бы
                // альбомы (у них тоже есть id+title) и служебные объекты блоков.
                if ((element.TryGetProperty("artists", out _) || element.TryGetProperty("albums", out _))
                    && ParseTrackObject(element) is { } track)
                {
                    if (seen.Add(track.Id)) into.Add(track);
                    return; // трек-объект внутрь не углубляемся
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
    /// Ответ GET /tracks/{id}/similar: {"result":[{…сид…, "similarTracks":[…track…]}]}
    /// (встречается и обёртка {"result":{"similarTracks":[…]}}). Особенность раскладки:
    /// кандидаты лежат ВНУТРИ объекта-трека (сида), поэтому обычный DFS лендинга не
    /// подходит — он не углубляется в трек-объекты. Здесь объект с "similarTracks"
    /// отдаёт только содержимое этого массива (сам сид не собирается), остальное дерево
    /// обходится рекурсивно; внутри массива кандидаты разбираются обычным DFS. Дубликаты
    /// по id отбрасываются, сам сид (seedYmId) исключается. Пустой список — не
    /// сошлось/нет похожих.
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
                // Массив similarTracks: кандидаты — обычные трек-объекты (артисты/альбомы
                // обязаны быть — см. CollectTrackObjects). Сам объект-обёртка (сид и
                // служебные поля) не собирается.
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

    // ========================= Поиск (/search) =========================

    /// <summary>
    /// Ответ GET /search?text=…&amp;type=track: {"result":{"tracks":{"results":[…track…],
    /// "total":…}, …}}. Сначала точный путь result.tracks.results; если раскладка не
    /// сошлась — DFS-фолбэк по всему дереву (тот же, что у similar). Чистая функция —
    /// покрыта юнит-тестами.
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
    /// Ответ GET /search?text=…&amp;type=artist: {"result":{"artists":{"results":[…artist…]}}}.
    /// Точный путь result.artists.results, фолбэк — DFS по объектам-исполнителям
    /// (id + имя + признак артиста: cover/various/composer/genres — отсекает label'ы
    /// с той же парой id+name). Чистая функция — покрыта юнит-тестами.
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

    // ========================= Исполнители (artists/{id}/…) =========================

    /// <summary>
    /// Ответ GET /artists/{id}/tracks?page=…&amp;pageSize=…:
    /// {"result":{"pager":{…},"tracks":[…track…]}} — треки исполнителя (каталог,
    /// НЕ лайки пользователя). Точный путь result.tracks, фолбэк — DFS по дереву.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Ответ GET /artists/{id}/brief-info: {"result":{"stats":{"lastMonthListeners":…},…}} —
    /// аудитория исполнителя за месяц (именно так его показывает карточка артиста).
    /// Фолбэк на "listeners" на случай изменения API. Фильтр ноунеймов/«нейро-треков».
    /// null — поле отсутствует (неизвестно). Чистая функция — покрыта юнит-тестами.
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

        // Актуальное поле API — lastMonthListeners (месячная аудитория карточки).
        if (stats.TryGetProperty("lastMonthListeners", out var month)
            && month.ValueKind == JsonValueKind.Number
            && month.TryGetInt64(out var monthValue))
            return monthValue;

        // Фолбэк: прежнее имя поля.
        if (stats.TryGetProperty("listeners", out var listeners)
            && listeners.ValueKind == JsonValueKind.Number
            && listeners.TryGetInt64(out var value))
            return value;

        return null;
    }

    /// <summary>
    /// Ответ GET /artists/{id}/similar: {"result":{"artist":{…},
    /// "similarArtists":[…artist…]}} — похожие исполнители («общая тусовка»):
    /// элементы массива — сами объекты артистов. Точный путь result.similarArtists,
    /// фолбэк — DFS по объектам-исполнителям. Чистая функция — покрыта юнит-тестами.
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
    /// Ответ GET /rotor/station/artist:{id}/tracks: {"result":{"sequence":[
    /// {"__type":"track","track":{…track…}}, …], "batchId":…}} — персональная радио-
    /// лента по исполнителю («похожий звук»). Треки собираются DFS-ом по дереву
    /// (кандидат — объект с id+title+artists/albums), дубликаты отбрасываются.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Разбор объекта-исполнителя: есть "id" (строка/число) и строковое "name".
    /// null — не сошлось.
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
    /// DFS-сбор объектов-исполнителей по дереву: id + name + хотя бы один признак
    /// артиста (cover/various/composer/genres) — отсекает label'ы и прочие объекты
    /// с парой id+name. Дубликаты по id отбрасываются.
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

    // ========================= Лайки (users/{uid}/likes/tracks) =========================

    /// <summary>
    /// Ответ GET /users/{uid}/likes/tracks: {"result":{"library":{"tracks":[{"id":…,"timestamp":…}, …]}}}.
    /// ВАЖНО: лайки возвращают ТОЛЬКО id — полные объекты добираются батчами через /tracks.
    /// id бывает числом и строкой. Если обёртка library неожиданно отсутствует — DFS-фолбэк
    /// по объектам с полями id+timestamp (раскладка меняется).
    /// </summary>
    /// <summary>Запись лайка: id трека (+возможный суффикс) и время лайка из API
    /// (ISO 8601, напр. "2026-09-30T15:20:20+00:00"; пусто — сервер не отдал).</summary>
    public sealed record YmLikeEntry(string Id, string LikedAt);

    /// <summary>
    /// Лайки с временем: id + timestamp записи. Основная раскладка —
    /// {"result":{"library":{"tracks":[{id, timestamp}, …]}}}; фолбэк — поиск по
    /// объектам с полями id+timestamp (раскладка меняется).
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

        // Фолбэк: ищем пары id+timestamp по всему дереву.
        CollectLikeEntries(root, result);
        return result;
    }

    /// <summary>Только id лайков (без времени) — обёртка над <see cref="ParseLikeEntries"/>.</summary>
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

    // ========================= Аккаунт (account/status) =========================

    /// <summary>
    /// Ответ GET /account/status: {"result":{"account":{"uid":…,"login":…,"fullName":…,
    /// "displayName":{"name":…}, …}}}. Приоритет имени: fullName → displayName.name → login.
    /// null — раскладка не сошлась (не аккаунт-ответ).
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
    /// Ответ GET /tracks/{id}/download-info. Раскладка вариантов менялась несколько раз —
    /// парсим lenient, на каждый элемент result[] пытаемся получить ссылку:
    ///   0) поле "downloadInfoUrl" (раскладка 2025+, поле bitrateInKbps) — ссылка на
    ///      XML-дескриптор, финальный URL собирается сервисом через ParseDownloadInfoXml;
    ///   1) массив "urls" с прямыми ссылками (transports=encode_info_websonic,pure_d);
    ///   2) поле "url": прямая https-ссылка ИЛИ шаблон с "$" — "$" раскрывается через
    ///      host/path того же элемента ("$host$path" → https://host + path);
    ///   3) классическая схема {host, path, ts, s} → "https://{host}/get-mp3/{s}/{ts}{path}".
    /// Элементы без вычислимой ссылки/дескриптора пропускаются (lenient-договорённость).
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

            // 0) Современная раскладка: дескриптор вместо готовой ссылки.
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
    /// XML-дескриптор по ссылке downloadInfoUrl:
    /// &lt;download-info&gt;&lt;host&gt;…&lt;/host&gt;&lt;path&gt;…&lt;/path&gt;&lt;ts&gt;…&lt;/ts&gt;&lt;s&gt;…&lt;/s&gt;&lt;/download-info&gt;.
    /// null — мусор/не хватает полей. Чистая функция — покрыта юнит-тестами.
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
            return null; // не XML (заглушка прокси/обрыв) — lenient
        }
    }

    /// <summary>Финальная mp3-ссылка из полей дескриптора: https://{host}/get-mp3/{s}/{ts}{path}.</summary>
    public static string BuildDownloadUrlFromXml(YmDownloadInfoXml info)
    {
        var slash = info.Path.StartsWith('/') ? string.Empty : "/";
        return $"https://{info.Host}/get-mp3/{info.S}/{info.Ts}{slash}{info.Path}";
    }

    /// <summary>Сборка ссылки из одного элемента download-info; null — не сошлось (см. доксуммарку класса).</summary>
    internal static string? BuildDownloadUrl(JsonElement item)
    {
        var host = GetString(item, "host");
        var path = GetString(item, "path");

        // 1) Массив готовых ссылок "urls".
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

        // 2) Одиночное поле "url": прямая ссылка или шаблон с "$".
        var urlField = GetString(item, "url");
        if (!string.IsNullOrWhiteSpace(urlField))
        {
            var expanded = ExpandPlaceholders(urlField, host, path);
            if (!string.IsNullOrEmpty(expanded)) return expanded;
        }

        // 3) Классическая схема {host, path, ts, s} → https://{host}/get-mp3/{s}/{ts}{path}.
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
    /// Раскрытие шаблона с "$": "$host" → host, "$path" → path; оставшийся одиночный "$"
    /// подставляет host+path целиком. Результат без схемы дополняется "https://".
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

    // ========================= Поля трека =========================

    /// <summary>
    /// Разбор одного объекта-трека. Трек-кандидат: есть "id" и строковый "title".
    /// artist — первые до трёх имён artists[].name через ", " (как в веб-плеере Яндекса);
    /// обложка — albums[0].coverUri (шаблон с "%%"); available отсутствует → true.
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

    /// <summary>Имена исполнителей через ", ", максимум три (веб-плеер Яндекса сворачивает так же).</summary>
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

    /// <summary>albums[0].coverUri (шаблон с "%%" вместо размера); null — обложки нет.</summary>
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
    /// Готовая ссылка на обложку из шаблона coverUri: "%%" заменяется на размер
    /// (например "300x400"/"400x400"); URI без схемы дополняется "https://".
    /// Плейсхолдера "%%" нет — строка используется как есть (уже полный URL).
    /// </summary>
    public static string? BuildArtworkUrl(string? coverUri, string size = "300x300")
    {
        if (string.IsNullOrWhiteSpace(coverUri)) return null;

        var url = coverUri.Contains("%%", StringComparison.Ordinal)
            ? coverUri.Replace("%%", size, StringComparison.Ordinal)
            : coverUri;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    // ========================= Примитивы =========================

    // ========================= OAuth Device Flow ================================

    /// <summary>
    /// Тело 200-ответа POST oauth.yandex.ru/device-code:
    /// {"device_code":"…","user_code":"…","verification_url":"https://oauth.yandex.ru/device",
    ///  "expires_in":300,"interval":5}. null — тело не распознано (сеть/мусор).
    /// Чистая функция — покрыта юнит-тестами.
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
    /// Тело ответа POST oauth.yandex.ru/token: 200 — {"access_token":"…"[,"expires_in":…]},
    /// 400 — {"error":"authorization_pending"|"slow_down"|…}. Не-JSON мусор (HTML-заглушка
    /// прокси и пр.) → ErrorCode="invalid_response", чтобы окно показало общий сбой, а не
    /// висело в ожидании.
    /// Чистая функция — покрыта юнит-тестами.
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

    // ============================ Хелперы =======================================

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null; // мусор/обрыв — пустой результат, не исключение
        }
    }

    internal static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Строковое поле, которое API отдаёт и числом, и строкой (id, uid).</summary>
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
