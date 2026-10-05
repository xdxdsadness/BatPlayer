using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Добывает публичный client_id веб-плеера SoundCloud.
/// Способ: GET https://soundcloud.com → ссылки на assets JS → скачать 1-2 скрипта →
/// регуляркой вытащить "client_id":"...". Результат кэшируется в памяти (TTL ~1 час)
/// и сохраняется в sc_auth.json как последний валидный — чтобы не скрапить при каждом старте.
/// Сеть — общий слой SoundCloudHttp (direct/прокси с фолбэком), своего HttpClient нет.
/// </summary>
public sealed partial class SoundCloudClientIdProvider
{
    /// <summary>TTL памяти: дольше держать бессмысленно, API успевает ротировать id.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private const string PageUrl = "https://soundcloud.com/";

    // Сколько assets-скриптов максимум качать. На текущем soundcloud.com их ~9,
    // client_id лежит в 5-м (порядок не гарантирован) — качаем до 12 параллельно.
    private const int MaxAssetScripts = 12;

    // "client_id":"abc..." — основной вид в минифицированных assets.
    [GeneratedRegex("\"client_id\"\\s*:\\s*\"([A-Za-z0-9]{20,40})\"")]
    private static partial Regex QuotedClientIdRegex();

    // client_id:"abc..." / client_id="abc..." — встречается в сгенерированном коде без кавычек у ключа.
    [GeneratedRegex("client_id\\s*[:=]\\s*\"([A-Za-z0-9]{20,40})\"")]
    private static partial Regex BareClientIdRegex();

    // ...?client_id=abc... — id в ссылке внутри скрипта.
    [GeneratedRegex("[?&]client_id=([A-Za-z0-9]{20,40})")]
    private static partial Regex UrlClientIdRegex();

    // Ссылки на JS-бандлы плеера в HTML главной страницы.
    [GeneratedRegex("https://a-v2\\.sndcdn\\.com/assets/[A-Za-z0-9_\\-\\.]+\\.js")]
    private static partial Regex AssetUrlRegex();

    private readonly SoundCloudAuthStore _auth;

    private string? _cachedId;
    private DateTime _cachedExpiresUtc = DateTime.MinValue;
    private DateTime _lastScrapeUtc = DateTime.MinValue;

    /// <summary>Минимальный интервал между принудительными скрапами: каждый 404-ретрай
    /// с forceRefresh раньше бил по главной SC (страница + 9 assets ≈ секунда) —
    /// на мёртвых треках это давало зависание при переключении.</summary>
    private static readonly TimeSpan ForceScrapeMinInterval = TimeSpan.FromSeconds(60);

    public SoundCloudClientIdProvider(SoundCloudAuthStore auth)
    {
        _auth = auth;
    }

    /// <summary>
    /// Возвращает client_id: из памяти → из файла → свежий скрап.
    /// forceRefresh=true (после 401/403) скрапит принудительно; при неудаче возвращает null.
    /// </summary>
    public async Task<string?> GetClientIdAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh)
        {
            if (!string.IsNullOrEmpty(_cachedId) && DateTime.UtcNow < _cachedExpiresUtc)
                return _cachedId;

            // Последний валидный из файла: экономим сеть на старте приложения.
            var stored = _auth.Load().ClientId;
            if (!string.IsNullOrEmpty(stored))
            {
                Remember(stored);
                return stored;
            }
        }
        else if (!string.IsNullOrEmpty(_cachedId)
                 && DateTime.UtcNow - _lastScrapeUtc < ForceScrapeMinInterval)
        {
            // Свежий скрап уже был — id почти наверняка тот же; повторный скрап
            // только добавляет секунду задержки. Отдаём текущий.
            return _cachedId;
        }

        var scraped = await ScrapeAsync(ct);
        if (scraped == null) return null;

        Remember(scraped);
        _lastScrapeUtc = DateTime.UtcNow;
        PersistClientId(scraped);
        return scraped;
    }

    private void Remember(string id)
    {
        _cachedId = id;
        _cachedExpiresUtc = DateTime.UtcNow + CacheTtl;
    }

    private void PersistClientId(string id)
    {
        var file = _auth.Load();
        file.ClientId = id;
        _auth.Save(file);
    }

    /// <summary>
    /// Скрап client_id с главной страницы и её assets-скриптов.
    /// Любая сетевая/разборная ошибка — null (вызывающий покажет ошибку в UI/лог).
    /// </summary>
    private async Task<string?> ScrapeAsync(CancellationToken ct)
    {
        try
        {
            var (pageStatus, html) = await SendAsync(PageUrl, ct);
            if (!IsSuccess(pageStatus))
            {
                Logger.Warn($"SoundCloud main page returned HTTP {pageStatus}");
                return null;
            }

            // Иногда client_id успевают отдать прямо в HTML.
            var inline = ExtractClientId(html);
            if (inline != null) return inline;

            var assets = ExtractAssetUrls(html);
            var selected = assets.Take(MaxAssetScripts).ToList();
            Logger.Info($"SoundCloud client_id scrape: {assets.Count} assets on page, fetching {selected.Count}");

            // Параллельно: тяжёлые бандлы качаются одновременно, а не по очереди.
            var fetches = selected.Select(a => SendAsync(a, ct)).ToArray();
            try
            {
                await Task.WhenAll(fetches);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ошибки отдельных ассетов обработаны ниже */ }

            foreach (var fetch in fetches)
            {
                if (fetch.IsFaulted || fetch.IsCanceled) continue;
                var (assetStatus, js) = fetch.Result;
                if (!IsSuccess(assetStatus)) continue;
                var id = ExtractClientId(js);
                if (id != null) return id;
            }

            Logger.Warn($"SoundCloud client_id not found in {selected.Count} fetched assets");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud client_id scrape failed");
            return null;
        }
    }

    /// <summary>GET через общий сетевой слой SoundCloudHttp (direct/прокси с фолбэком).</summary>
    private static Task<(int Status, string Body)> SendAsync(string url, CancellationToken ct)
        => SoundCloudHttp.SendWithFailoverAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>Уникальные ссылки на assets JS в порядке появления в HTML.</summary>
    internal static List<string> ExtractAssetUrls(string html)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(html)) return result;

        foreach (Match m in AssetUrlRegex().Matches(html))
        {
            var url = m.Value;
            if (!result.Contains(url)) result.Add(url);
        }
        return result;
    }

    /// <summary>
    /// Поиск client_id в куске JS/HTML. internal — для юнит-тестов (regex по реалистичному минифицированному коду).
    /// </summary>
    internal static string? ExtractClientId(string js)
    {
        if (string.IsNullOrEmpty(js)) return null;

        var m = QuotedClientIdRegex().Match(js);
        if (m.Success) return m.Groups[1].Value;

        m = BareClientIdRegex().Match(js);
        if (m.Success) return m.Groups[1].Value;

        m = UrlClientIdRegex().Match(js);
        return m.Success ? m.Groups[1].Value : null;
    }
}
