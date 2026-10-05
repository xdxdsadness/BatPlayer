using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Один трек, извлечённый из позиционного массива ответа al_audio.php (веб-эндпоинт
/// каталога музыки vk.com, тот же, которым пользуется веб-плеер). Часть полей может
/// отсутствовать — VK меняет раскладку позиций; надёжны только vk_id и наличие url.
/// </summary>
public sealed class ParsedWebAudio
{
    public long AudioId { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    /// <summary>Длительность в секундах; 0 — не удалось определить (позиция нестабильна).</summary>
    public long DurationSec { get; init; }
    /// <summary>Временная прямая ссылка mp3/hls; null — трек скрыт/удалён (неиграбелен).</summary>
    public string? StreamUrl { get; init; }
    /// <summary>Ссылка на обложку (sun*.userapi.com/*.jpg); null — карточка покажет плейсхолдер.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>Скрытые/удалённые записи (без url) неиграбельны и в каталог не попадают.</summary>
    public bool IsPlayable => !string.IsNullOrEmpty(StreamUrl);

    /// <summary>vk_id = "{owner_id}_{audio_id}" (тот же формат, что давал audio.get).</summary>
    public string VkId => $"{OwnerId}_{AudioId}";
}

/// <summary>
/// Результат разбора тела ответа al_audio.php.
/// </summary>
internal sealed record AlAudioPayload(
    IReadOnlyList<ParsedWebAudio> Tracks,
    /// <summary>Явный nextOffset из payload (VK отдаёт его не всегда); null — нет данных.</summary>
    int? NextOffset,
    /// <summary>Заявленный total раздела; 0 — в payload не нашёлся.</summary>
    int ReportedTotal,
    /// <summary>Код ошибки из payload[0].code (0 — успех); не ноль — VK вернул ошибку раздела.</summary>
    int ErrorCode,
    /// <summary>Хоть один JSON-чанк разобрался: false — ответ не al_audio вовсе (мусор/HTML).</summary>
    bool Parsed);

/// <summary>
/// Разбор ответа внутреннего веб-эндпоинта al_audio.php. Ответ — текст вида
/// `<!json>{"payload":[{"code":0,"data":[...]}]}`, где data содержит ПОЗИЦИОННЫЕ
/// массивы треков. Формат позиций VK регулярно меняет, поэтому разбор lenient:
///
///   • трек-кандидат — массив длиной ≥ <see cref="MinTrackArrayLength"/>, у которого
///     [0] — целое &gt; 0 (audio id), [1] — целое ≠ 0 (owner_id; у групповых записей
///     он отрицательный) и среди верхнеуровневых элементов есть строка "http…";
///   • сканируется всё дерево payload рекурсивно (список треков обычно data[1],
///     но позиция обёртки тоже меняется) — короткие служебные массивы фильтр не
///     проходят;
///   • из строк-элементов: первый http не-картинка — стрим (mp3/hls), первый
///     http-картинка (.jpg/.png/…) — обложка (если на верхнем уровне её нет —
///     неглубокий поиск во вложенных массивах, обложки живут в element[13]-like);
///   • artist/title — первые две не-http строки (VK всегда отдаёт artist раньше
///     title на всех известных раскладках 2/3 и 3/4); одна строка — попытка
///     разделить "Artist - Title";
///   • duration — первое целое после позиции title в диапазоне 1..86400.
///
/// При первом успешном разборе в лог пишется один сырой элемент (обрезанный до
/// 500 символов) — диагностика раскладки на живом ответе. Cookies/токены в логи
/// не попадают (в payload трека их нет).
/// </summary>
/// <summary>Один трек из современной секции type=recent (кортежи-массивы).</summary>
public sealed class RecentAudioTuple
{
    public long AudioId { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public long DurationSec { get; init; }
    /// <summary>Хеш ссылки на поток: reload_audio строит из него mp3-ссылку.</summary>
    public string UrlHash { get; init; } = string.Empty;
    /// <summary>Обложки через запятую (sun*.vkuserphoto.ru); пусто — плейсхолдер.</summary>
    public string ArtworkUrls { get; init; } = string.Empty;
}

internal static class PositionalAudioParser
{
    /// <summary>Минимальная длина массива-кандидата в трек (реальные элементы ≥ 12 полей).</summary>
    internal const int MinTrackArrayLength = 12;

    /// <summary>Разумная верхняя граница duration (сек) — отсев id/флагов при поиске.</summary>
    private const long MaxDurationSec = 86400;

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    private static bool _rawLogged;

    // ========================= Тело ответа =========================

       /// <summary>
    /// Полный разбор тела ответа al_audio.php (возможно несколько `&lt;!json&gt;`-чанков).
    /// Мусор/HTML не бросают исключений — возвращается пустой результат; протухшую
    /// сессию до этого этапа ловит <see cref="IsLoginHtml"/>.
    /// </summary>
    internal static AlAudioPayload ParsePayload(string body)
    {
        var tracks = new List<ParsedWebAudio>();
        int? nextOffset = null;
        int total = 0;
        int errorCode = 0;
        var parsed = false;

        foreach (var chunk in SplitJsonChunks(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(chunk);
                parsed = true;
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload)
                    && payload.ValueKind == JsonValueKind.Array)
                {
                    // Код ошибки раздела берём только с верхнеуровневых элементов payload
                    // ({"code":N,"data":[...]}): вложенные "code" других объектов не трогаем.
                    foreach (var item in payload.EnumerateArray())
                    {
                        // Челлендж авторизации приходит СТРОКОЙ-статусом: {"payload":["3",[...]]}.
                        if (item.ValueKind == JsonValueKind.String
                            && item.GetString() is { } status && status != "0")
                        {
                            if (int.TryParse(status, out var sc) && errorCode == 0)
                                errorCode = sc;
                            continue;
                        }
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (item.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c != 0)
                        {
                            errorCode = errorCode == 0 ? c : errorCode;
                        }
                    }
                }

                CollectTracks(root, tracks);
                CollectNumbers(root, ref nextOffset, ref total);
            }
            catch (JsonException ex)
            {
                // Чанк не JSON (обрыв/мусор) — остальные чанки всё равно пробуем.
                Logger.Error(ex, "VK al_audio JSON chunk parse failed");
            }
        }

        return new AlAudioPayload(tracks, nextOffset, total, errorCode, parsed);
    }

    /// <summary>
    /// VK вернул страницу логина (HTML с формой входа) — cookies веб-сессии протухли.
    /// Пустой ответ логином не считается (это транспортная проблема, а не сессия).
    /// </summary>
    internal static bool IsLoginHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("<!json>", StringComparison.Ordinal)) return false;
        if (!trimmed.StartsWith('<')) return false;

        return trimmed.Contains("login", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разрезает тело на JSON-чанки: каждый сегмент после `&lt;!json&gt;`, начинающийся
    /// с '{' (границы — до последнего '}', хвостовой мусор отрезается). Без маркера
    /// целиком считаем чанком, только если тело начинается с '{'.
    /// </summary>
    private static IEnumerable<string> SplitJsonChunks(string body)
    {
        const string marker = "<!json>";
        var segments = body.Contains(marker, StringComparison.Ordinal)
            ? body.Split(marker)
            : [body];

        foreach (var segment in segments)
        {
            var start = segment.IndexOf('{');
            var end = segment.LastIndexOf('}');
            if (start < 0 || end <= start) continue;
            yield return segment[start..(end + 1)];
        }
    }

    // ====================== Поиск трек-массивов ====================

    /// <summary>DFS по дереву JSON: кандидаты собираются, их вложенности не обходятся.</summary>
    private static void CollectTracks(JsonElement element, List<ParsedWebAudio> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                if (IsTrackCandidate(element))
                {
                    var track = ParseTrackElement(element);
                    if (track != null)
                    {
                        into.Add(track);
                        return; // внутри трека других треков нет — не обходим
                    }
                }
                foreach (var child in element.EnumerateArray())
                    CollectTracks(child, into);
                break;

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    CollectTracks(prop.Value, into);
                break;
        }
    }

    /// <summary>
    /// nextOffset/total ищутся где угодно в payload (VK кладёт их в служебные объекты
    /// data — позиция меняется). Берутся первые найденные значения.
    /// </summary>
    private static void CollectNumbers(JsonElement element, ref int? nextOffset, ref int total)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;

                    if (nextOffset == null
                        && string.Equals(prop.Name, "nextOffset", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.TryGetInt32(out var next) && next >= 0)
                    {
                        nextOffset = next;
                    }
                    else if (total == 0
                        && string.Equals(prop.Name, "total", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.TryGetInt32(out var t) && t > 0)
                    {
                        total = t;
                    }
                }
                foreach (var prop in element.EnumerateObject())
                    CollectNumbers(prop.Value, ref nextOffset, ref total);
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectNumbers(child, ref nextOffset, ref total);
                break;
        }
    }

    /// <summary>Трек-кандидат: длина ≥ 12, [0] — целое &gt; 0, [1] — целое ≠ 0, есть http-строка.</summary>
    internal static bool IsTrackCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return false;
        if (element.GetArrayLength() < MinTrackArrayLength) return false;

        if (!TryGetArrayInt(element, 0, out var audioId) || audioId <= 0) return false;
        if (!TryGetArrayInt(element, 1, out var ownerId) || ownerId == 0) return false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && IsHttpString(item.GetString()))
                return true;
        }
        return false;
    }

    private static bool TryGetArrayInt(JsonElement array, int index, out long value)
    {
        value = 0;
        if (array.ValueKind != JsonValueKind.Array) return false;
        var length = array.GetArrayLength();
        if (index < 0 || index >= length) return false;
        var item = array[index];
        if (item.ValueKind != JsonValueKind.Number) return false;
        return item.TryGetInt64(out value);
    }

    // ===================== Поля трека (lenient) ====================

    /// <summary>
    /// Поля по правилам из доксуммарки типа. null — кандидат не сошёлся (нулевые id).
    /// </summary>
    internal static ParsedWebAudio? ParseTrackElement(JsonElement element)
    {
        if (!TryGetArrayInt(element, 0, out var audioId) || audioId <= 0) return null;
        if (!TryGetArrayInt(element, 1, out var ownerId) || ownerId == 0) return null;

        string? streamUrl = null;
        string? artworkUrl = null;
        var texts = new List<(int index, string value)>();
        var titleIndex = -1;

        var index = -1;
        foreach (var item in element.EnumerateArray())
        {
            index++;
            if (index < 2) continue; // [0]/[1] — идентификаторы

            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (string.IsNullOrEmpty(s)) continue;

                if (IsHttpString(s))
                {
                    if (IsImageUrl(s))
                        artworkUrl ??= s;
                    else
                        streamUrl ??= s;
                }
                else
                {
                    texts.Add((index, s));
                }
            }
        }

        string artist, title;
        if (texts.Count >= 2)
        {
            // VK всегда отдаёт artist раньше title (позиции 2/3 или 3/4 на известных раскладках).
            artist = texts[0].value;
            title = texts[1].value;
            titleIndex = texts[1].index;
        }
        else if (texts.Count == 1)
        {
            // Иногда VK склеивает "Artist - Title" (subtitle-режим) — делим по первому " - ".
            var dash = texts[0].value.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = texts[0].value[..dash];
                title = texts[0].value[(dash + 3)..];
            }
            else
            {
                artist = string.Empty;
                title = texts[0].value;
            }
            titleIndex = texts[0].index;
        }
        else
        {
            // Нестандартная раскладка: названия не на верхнем уровне — оставляем пустыми,
            // в лог уходит сырой элемент (см. LogRawElement) для разбора позиции.
            artist = string.Empty;
            title = string.Empty;
        }

        // duration — первое целое сразу после позиции title (на известных раскладках
        // идёт соседним полем); при неизвестной позиции — поиск с индекса 2.
        var duration = 0L;
        var searchFrom = titleIndex >= 0 ? titleIndex + 1 : 2;
        for (var i = searchFrom; i < element.GetArrayLength(); i++)
        {
            if (TryGetArrayInt(element, i, out var v) && v > 0 && v <= MaxDurationSec)
            {
                duration = v;
                break;
            }
        }

        // Обложка не на верхнем уровне — неглубокий поиск во вложенных массивах
        // (element[13]-like): трековые вложенности — альбом/исполнители.
        if (artworkUrl == null)
            artworkUrl = FindNestedImage(element, depth: 0);

        LogRawElement(element);

        return new ParsedWebAudio
        {
            AudioId = audioId,
            OwnerId = ownerId,
            Title = title,
            Artist = artist,
            DurationSec = duration,
            StreamUrl = streamUrl,
            ArtworkUrl = artworkUrl
        };
    }

    private static string? FindNestedImage(JsonElement element, int depth)
    {
        if (depth > 3) return null;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String
                    && IsHttpString(child.GetString())
                    && IsImageUrl(child.GetString()))
                {
                    return child.GetString();
                }
                var nested = FindNestedImage(child, depth + 1);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var nested = FindNestedImage(prop.Value, depth + 1);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static bool IsHttpString(string? s)
        => !string.IsNullOrEmpty(s)
           && s.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>Похоже ли на картинку (расширение до '?'; стрим-ссылки mp3/m3u8 отсюда исключены).</summary>
    private static bool IsImageUrl(string? s)
    {
        if (IsHttpString(s) == false) return false;

        var query = s!.IndexOf('?');
        var path = query >= 0 ? s[..query] : s;
        foreach (var ext in ImageExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Диагностика живой раскладки: один раз за процесс логируем сырой элемент
    /// (до 500 символов) первого разобранного трека. Позиции полей в нём видны явно.
    /// </summary>
    private static void LogRawElement(JsonElement element)
    {
        if (_rawLogged) return;
        _rawLogged = true;

        try
        {
            var raw = element.GetRawText();
            if (raw.Length > 500) raw = raw[..500] + "…";
            Logger.Info($"VK al_audio track layout sample: {raw}");
        }
        catch (Exception ex)
        {
            // Диагностика не должна ронять разбор.
            Logger.Error(ex, "VK al_audio raw element logging failed");
        }
    }

    /// <summary>
    /// Парс современной секции type=recent: payload[0] — код (0 = успех), payload[1][0] —
    /// объект секции с "list" — массив кортежей треков:
    ///   [0]=id, [1]=owner_id, [3]=title, [4]=artist, [5]=duration, [13]=url hash,
    ///   [14]=обложки через запятую.
    /// Мусор/неожиданная раскладка — пустой результат без исключений.
    /// </summary>
    internal static (int ErrorCode, List<RecentAudioTuple> Tracks) ParseRecentSection(string? body)
    {
        var tracks = new List<RecentAudioTuple>();
        var errorCode = 0;

        if (string.IsNullOrWhiteSpace(body)) return (0, tracks);

        var json = body.Trim();
        var jsonStart = json.IndexOf("<!json>", StringComparison.Ordinal);
        if (jsonStart >= 0) json = json[(jsonStart + 7)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Array
                || payload.GetArrayLength() < 2)
            {
                return (0, tracks);
            }

            // Код статуса: число или числовая строка в payload[0]
            var status = payload[0];
            if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code != 0)
                errorCode = code;
            else if (status.ValueKind == JsonValueKind.String
                     && int.TryParse(status.GetString(), out var sc) && sc != 0)
                errorCode = sc;

            if (errorCode != 0) return (errorCode, tracks);

            var data = payload[1];
            if (data.ValueKind != JsonValueKind.Array) return (0, tracks);

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array) continue;

                foreach (var tuple in list.EnumerateArray())
                {
                    if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 6) continue;

                    long audioId = tuple[0].ValueKind == JsonValueKind.Number ? tuple[0].GetInt64() : 0;
                    long ownerId = tuple[1].ValueKind == JsonValueKind.Number ? tuple[1].GetInt64() : 0;
                    var title = tuple[3].ValueKind == JsonValueKind.String ? tuple[3].GetString() ?? "" : "";
                    var artist = tuple[4].ValueKind == JsonValueKind.String ? tuple[4].GetString() ?? "" : "";
                    long duration = tuple[5].ValueKind == JsonValueKind.Number ? tuple[5].GetInt64() : 0;
                    var urlHash = tuple.GetArrayLength() > 13 && tuple[13].ValueKind == JsonValueKind.String
                        ? tuple[13].GetString() ?? "" : "";
                    var covers = tuple.GetArrayLength() > 14 && tuple[14].ValueKind == JsonValueKind.String
                        ? tuple[14].GetString() ?? "" : "";

                    if (audioId == 0 || string.IsNullOrEmpty(title)) continue;

                    tracks.Add(new RecentAudioTuple
                    {
                        AudioId = audioId,
                        OwnerId = ownerId,
                        Title = title,
                        Artist = artist,
                        DurationSec = Math.Max(0, duration),
                        UrlHash = urlHash,
                        ArtworkUrls = covers
                    });
                }
            }
        }
        catch (JsonException)
        {
            // мусор/обрыв — пустой результат
        }

        return (errorCode, tracks);
    }
}
