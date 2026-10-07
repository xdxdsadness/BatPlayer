using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Scrapes the public client_id of the SoundCloud web player: GET https://soundcloud.com →
/// asset JS links → download 1-2 scripts → regex out "client_id":"...". The result is cached
/// in memory (TTL ~1h) and persisted to sc_auth.json as the last valid id, so the app does not
/// scrape on every start. Networking goes through the shared SoundCloudHttp layer
/// (direct/proxy with fallback); no own HttpClient.
/// </summary>
public sealed partial class SoundCloudClientIdProvider
{
    /// <summary>In-memory TTL: keeping it longer is pointless, the API rotates ids.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private const string PageUrl = "https://soundcloud.com/";

    // Max asset scripts to fetch. The current soundcloud.com has ~9 and client_id sits in
    // the 5th (order not guaranteed) — fetch up to 12 in parallel.
    private const int MaxAssetScripts = 12;

    // "client_id":"abc..." — the common form in minified assets.
    [GeneratedRegex("\"client_id\"\\s*:\\s*\"([A-Za-z0-9]{20,40})\"")]
    private static partial Regex QuotedClientIdRegex();

    // client_id:"abc..." / client_id="abc..." — occurs in generated code with an unquoted key.
    [GeneratedRegex("client_id\\s*[:=]\\s*\"([A-Za-z0-9]{20,40})\"")]
    private static partial Regex BareClientIdRegex();

    // ...?client_id=abc... — id in a URL inside a script.
    [GeneratedRegex("[?&]client_id=([A-Za-z0-9]{20,40})")]
    private static partial Regex UrlClientIdRegex();

    // Player JS bundle links in the landing page HTML.
    [GeneratedRegex("https://a-v2\\.sndcdn\\.com/assets/[A-Za-z0-9_\\-\\.]+\\.js")]
    private static partial Regex AssetUrlRegex();

    private readonly SoundCloudAuthStore _auth;

    private string? _cachedId;
    private DateTime _cachedExpiresUtc = DateTime.MinValue;
    private DateTime _lastScrapeUtc = DateTime.MinValue;

    /// <summary>Min interval between forced scrapes: every 404 retry with forceRefresh used
    /// to hit the SC landing page (page + 9 assets ≈ a second) — dead tracks hung track switching.</summary>
    private static readonly TimeSpan ForceScrapeMinInterval = TimeSpan.FromSeconds(60);

    public SoundCloudClientIdProvider(SoundCloudAuthStore auth)
    {
        _auth = auth;
    }

    /// <summary>
    /// Returns the client_id: memory → file → fresh scrape.
    /// forceRefresh=true (after 401/403) forces a scrape; returns null on failure.
    /// </summary>
    public async Task<string?> GetClientIdAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh)
        {
            if (!string.IsNullOrEmpty(_cachedId) && DateTime.UtcNow < _cachedExpiresUtc)
                return _cachedId;

            // Last valid id from the file: saves network on app start.
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
            // A fresh scrape just happened — the id is almost certainly the same; another
            // scrape only adds a second of delay. Return the current one.
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
    /// Scrapes client_id from the landing page and its asset scripts.
    /// Any network/parse error — null (the caller reports the error in UI/log).
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

            // Sometimes the client_id is served right in the HTML.
            var inline = ExtractClientId(html);
            if (inline != null) return inline;

            var assets = ExtractAssetUrls(html);
            var selected = assets.Take(MaxAssetScripts).ToList();
            Logger.Info($"SoundCloud client_id scrape: {assets.Count} assets on page, fetching {selected.Count}");

            // In parallel: heavy bundles download concurrently instead of one by one.
            var fetches = selected.Select(a => SendAsync(a, ct)).ToArray();
            try
            {
                await Task.WhenAll(fetches);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* individual asset errors handled below */ }

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

    /// <summary>GET via the shared SoundCloudHttp network layer (direct/proxy with fallback).</summary>
    private static Task<(int Status, string Body)> SendAsync(string url, CancellationToken ct)
        => SoundCloudHttp.SendWithFailoverAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>Unique asset JS links in order of appearance in the HTML.</summary>
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
    /// Finds client_id in a JS/HTML chunk. internal — for unit tests (regex over realistic minified code).
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
