using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using BatPlayer.Localization;
using BatPlayer.Services;
using BatPlayer.Services.Vk;

namespace BatPlayer.Views;

/// <summary>
/// VK login window — web-session COOKIE authorization, same principle as the SoundCloud window.
/// The OAuth phase (oauth.vk.com/authorize + Kate Mobile) was removed: VK's audio.get tokens
/// are dead; the catalog is served by the web endpoint al_audio.php using site session cookies.
///
/// Flow: the regular mobile login page https://m.vk.com/login opens (no third-party app screen).
/// Success is detected by polling every 500ms — navigation to feed/al_im (vk.com AND vk.ru)
/// OR the appearance of the remixsid web-session cookie (the site may restore the session
/// without a redirect to feed). After detection:
///   1) cookies are collected from both domains (vk.ru + vk.com → "name=value; …");
///   2) userId is taken from the remixuid cookie, else by regex over the current page HTML
///      (ExecuteScriptAsync), else GET m.vk.ru/feed with cookies via HttpClient;
///   3) VkService.SaveSessionCookies(cookieHeader, userId), the window closes with DialogResult=true.
/// Cookies are never logged (only the user id and the fact the session was saved).
/// WebView2 goes DIRECT (--no-proxy-server): VK throttles login attempts from VPN exits.
/// If the WebView2 Runtime is missing — a localized error is shown, the app doesn't crash.
/// </summary>
    public partial class VkLoginWindow : Window
{
    private static readonly string[] CookieDomains = new[] { "https://vk.ru", "https://vk.com", "https://m.vk.com", "https://login.vk.ru" };

    /// <summary>Regular sign-in on the VK site (mobile version — reliable redirect to feed).</summary>
    private const string LoginUrl = "https://vk.ru/login";

    /// <summary>Domains whose cookies are collected (vk.ru — current, vk.com — legacy).</summary>
    private const string CookieDomainRu = "https://vk.ru";
    private const string CookieDomainCom = "https://vk.com";

    /// <summary>Cookie of an authorized VK web session.</summary>
    private const string SessionCookieName = "remixsid";

    /// <summary>Cookie holding the user id (value is the uid); not always present.</summary>
    private const string UserIdCookieName = "remixuid";

    /// <summary>Page whose HTML is used to extract the uid (and the request Referer).</summary>
    private const string FeedUrl = "https://m.vk.ru/feed";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly VkService _vk;
    private DispatcherTimer? _pollTimer;
    private bool _checking;
    private bool _finished;
    private bool _audioWarmupDone;

    public VkLoginWindow(VkService vk)
    {
        InitializeComponent();
        _vk = vk;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _pollTimer?.Stop();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // WebView2 data folder lives in %LOCALAPPDATA%/BatPlayer: the exe folder may be read-only.
            var userDataFolder = System.IO.Path.Combine(App.AppDataDir, "webview2", "vk");
            // VK is reachable directly from the user's region; via VPN exits VK throttles login
            // attempts ("Too many attempts" right after the phone step) — go DIRECT.
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--no-proxy-server --proxy-bypass-list=<-loopback>"
            };
            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder,
                    options: envOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Profile folder locked by another instance or corrupted ("cache error") —
                // retry with a unique folder; the login runs fresh anyway.
                Logger.Warn($"WebView2 profile folder unavailable, using a fresh one: {ex.Message}");
                userDataFolder += "_" + Guid.NewGuid().ToString("N")[..8];
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder,
                    options: envOptions);
            }
            await Web.EnsureCoreWebView2Async(env);

            await ClearPlatformCookiesAsync(Web.CoreWebView2);

            Web.Source = new Uri(LoginUrl);

            // Poll cookies/navigation: login can complete without the event we need
            // (SPA redirects, restored session) — the timer runs while the window is open.
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += PollSessionTick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            // Most common case: WebView2 Runtime not installed; the error text is localized.
            Logger.Error(ex, "VK login: WebView2 init failed");
            ShowWebviewMissing();
        }
    }

    /// <summary>
    /// Poll: login succeeded when the WebView reached feed/al_im OR the web-session cookie appeared.
    /// Idempotent (_finished flag); ticks after closing do nothing.
    /// </summary>
    private async void PollSessionTick(object? sender, EventArgs e)
    {
        if (_finished || _checking || Web.CoreWebView2 == null) return;
        _checking = true;
        try
        {
            var source = Web.Source?.ToString();
            var cookiesRu = await Web.CoreWebView2.CookieManager.GetCookiesAsync(CookieDomainRu);
            var cookiesCom = await Web.CoreWebView2.CookieManager.GetCookiesAsync(CookieDomainCom);
            var pairsRu = cookiesRu.Select(c => (c.Name, c.Value));
            var pairsCom = cookiesCom.Select(c => (c.Name, c.Value));

            // Audio-section warmup: after login VK requires a visit to vk.ru/audio —
            // without it al_audio.php answers with code 3 (challenge) instead of the catalog.
            if (!_audioWarmupDone)
            {
                if (!IsLoggedIn(source, pairsRu, pairsCom)) return;
                _audioWarmupDone = true;
                Web.Source = new Uri("https://vk.ru/audio");
                return; // cookies are collected after the audio section loads
            }

            // Saving — once the warmup is done: any vk.ru/vk.com page EXCEPT the login
            // procedure itself (login.*). After /audio VK may keep the address as-is (SPA) —
            // that also counts as settled.
            var settled = source != null
                          && (source.Contains("vk.ru/", StringComparison.OrdinalIgnoreCase)
                              || source.Contains("vk.com/", StringComparison.OrdinalIgnoreCase))
                          && !source.Contains("login.", StringComparison.OrdinalIgnoreCase);
            if (!settled) return;

            var cookieHeader = BuildCookieHeader(pairsRu, pairsCom);
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return; // cookies not settled yet — retry on the next tick

            // Preferred id source is the audio page address vk.ru/audios{uid};
            // fallbacks are the current page HTML (uid/viewer_id) and the feed.
            string? userId = null;
            if (source != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(source, @"/audios(\d+)");
                if (m.Success) userId = m.Groups[1].Value;
            }
            userId ??= await ResolveUserIdAsync(pairsRu, pairsCom, cookieHeader);
            await FinishAsync(cookieHeader, userId);
        }
        catch (Exception ex)
        {
            // A single poll failure doesn't close the window — retry on the next tick.
            Logger.Error(ex, "VK login: session poll failed");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Login succeeded: feed/al_im on any VK domain, or the web-session cookie.
    /// Pure function (name/value pairs instead of CoreWebView2Cookie) — covered by unit tests.</summary>
    internal static bool IsLoggedIn(string? uri,
        IEnumerable<(string Name, string Value)> cookiesRu,
        IEnumerable<(string Name, string Value)> cookiesCom)
    {
        // VK uses both vk.ru and vk.com domains (including m.vk.*) — recognize both.
        var onVk = uri != null &&
                   (uri.Contains("vk.ru", StringComparison.OrdinalIgnoreCase) ||
                    uri.Contains("vk.com", StringComparison.OrdinalIgnoreCase));
        if (onVk && (uri!.Contains("/feed", StringComparison.OrdinalIgnoreCase)
                     || uri.Contains("/al_im", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return HasSessionCookie(cookiesRu) || HasSessionCookie(cookiesCom);
    }

    private static bool HasSessionCookie(IEnumerable<(string Name, string Value)> cookies)
        => cookies.Any(c => string.Equals(c.Name, SessionCookieName, StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(c.Value));

    /// <summary>
    /// Cookie string for vk.com/vk.ru: all vk.ru cookies first (current domain), then vk.com
    /// cookies missing by name. Empty names/values are skipped.
    /// Pure function — covered by unit tests.
    /// </summary>
    internal static string BuildCookieHeader(IEnumerable<(string Name, string Value)> cookiesRu,
                                             IEnumerable<(string Name, string Value)> cookiesCom)
    {
        var pairs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, value) in cookiesRu.Concat(cookiesCom))
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;
            if (!seen.Add(name)) continue;
            pairs.Add($"{name}={value}");
        }
        return string.Join("; ", pairs);
    }

    /// <summary>
    /// User id: remixuid cookie → current page HTML (ExecuteScriptAsync) → GET m.vk.ru/feed
    /// with cookies. null — not found (the session is saved without an id; catalog sync will
    /// require re-login); the found value is logged.
    /// </summary>
    private async Task<string?> ResolveUserIdAsync(IEnumerable<(string Name, string Value)> cookiesRu,
                                                   IEnumerable<(string Name, string Value)> cookiesCom,
                                                   string cookieHeader)
    {
        var fromCookie = cookiesRu.Concat(cookiesCom)
            .FirstOrDefault(c => string.Equals(c.Name, UserIdCookieName, StringComparison.Ordinal)
                                 && !string.IsNullOrEmpty(c.Value)
                                 && c.Value != "0").Value;
        if (!string.IsNullOrEmpty(fromCookie))
        {
            Logger.Info($"VK login: user id {fromCookie} (cookie {UserIdCookieName})");
            return fromCookie;
        }

        var fromPage = VkService.ExtractUserIdFromHtml(await ReadPageHtmlAsync());
        if (!string.IsNullOrEmpty(fromPage))
        {
            Logger.Info($"VK login: user id {fromPage} (page html)");
            return fromPage;
        }

        var fromFeed = VkService.ExtractUserIdFromHtml(await FetchFeedHtmlAsync(cookieHeader));
        if (!string.IsNullOrEmpty(fromFeed))
        {
            Logger.Info($"VK login: user id {fromFeed} (feed html)");
            return fromFeed;
        }

        Logger.Warn("VK login: user id could not be determined (remixuid cookie, page html and feed html all empty)");
        return null;
    }

    /// <summary>HTML of the current WebView document (ExecuteScriptAsync returns a JSON string).</summary>
    private async Task<string?> ReadPageHtmlAsync()
    {
        try
        {
            var json = await Web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            // ExecuteScriptAsync returns the result as JSON — unwrap it into a string.
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (Exception ex)
        {
            // The page may still be loading or failed — this is only one of the id sources.
            Logger.Error(ex, "VK login: page html read failed");
            return null;
        }
    }

    /// <summary>GET m.vk.ru/feed with the session cookies (same direct network layer as the catalog).</summary>
    private static async Task<string?> FetchFeedHtmlAsync(string cookieHeader)
    {
        try
        {
            var (status, body) = await VkHttp.SendWithFailoverAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                request.Headers.TryAddWithoutValidation("Referer", "https://m.vk.ru/");
                return request;
            }, CancellationToken.None);

            return status == 200 ? body : null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK login: feed html fetch failed");
            return null;
        }
    }

    /// <summary>Save the session and close the window (idempotent).</summary>
    private async Task FinishAsync(string cookieHeader, string? userId)
    {
        if (_finished) return;
        _finished = true;
        _pollTimer?.Stop();

        _vk.SaveSessionCookies(cookieHeader, userId);
        Logger.Info("VK login: web session cookies saved");

        await Task.CompletedTask;
        DialogResult = true;
        Close();
    }

    private void ShowWebviewMissing()
    {
        Web.Visibility = Visibility.Collapsed;
        WebviewMissingPanel.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* window may already be closed */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    /// <summary>
    /// Clear platform cookies before login: after Disconnect the persistent WebView2
    /// profile kept the old session and auto-login returned to the same account.
    /// </summary>
    private static async Task ClearPlatformCookiesAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            foreach (var uri in CookieDomains)
            {
                var cookies = await core.CookieManager.GetCookiesAsync(uri);
                foreach (var cookie in cookies)
                    core.CookieManager.DeleteCookie(cookie);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Login window: cookie cleanup failed");
        }
    }

}
