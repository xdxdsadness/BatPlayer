using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using BatPlayer.Localization;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;

namespace BatPlayer.Views;

/// <summary>
/// SoundCloud login window: an embedded WebView2 opens soundcloud.com/login. Success is
/// detected by the appearance of the oauth_token cookie for soundcloud.com (polled every
/// 500ms); afterwards all soundcloud.com cookies are collected into a cookie string and
/// handed to the service (persisting to sc_auth.json lives in SoundCloudLoginService).
/// Shows a clear error instead of crashing when the WebView2 Runtime is missing.
/// </summary>
    public partial class SoundCloudLoginWindow : Window
{
    /// <summary>Login page URL.</summary>
    private const string LoginUrl = "https://soundcloud.com/login";
    /// <summary>Logout ON THE SITE: opens SoundCloud's own pages so the user
    /// picks which account to sign in with (like a Google account chooser).</summary>
    private const string LogoutUrl = "https://soundcloud.com/logout";
    /// <summary>Domain whose cookies are collected.</summary>
    private const string CookieDomain = "https://soundcloud.com";
    /// <summary>Cookie whose appearance signals a successful login.</summary>
    private const string OAuthCookieName = "oauth_token";

    private readonly SoundCloudService _soundCloud;
    /// <summary>Sign out of the current on-site session first ("switch account" mode).</summary>
    private readonly bool _signOutFirst;
    private DispatcherTimer? _pollTimer;
    private bool _checking;
    private bool _finished;

    // oauth_token appears BEFORE the web login finishes writing the rest of the
    // session (sc_session, fresh datadome); saving immediately yields an incomplete
    // set, and MONETIZE-track media requires the full session. Wait after oauth_token
    // first appears until the cookie set stabilizes.
    private DateTime? _oauthFirstSeenUtc;
    private int _lastCookieCount = -1;

    /// <summary>How long to wait after oauth_token appears for the cookies to stop changing.</summary>
    private static readonly TimeSpan CookieStabilityDelay = TimeSpan.FromSeconds(3);
    /// <summary>Upper bound on waiting; an "unstable" set is saved anyway so login never hangs.</summary>
    private static readonly TimeSpan CookieMaxWait = TimeSpan.FromSeconds(8);

    /// <summary>Collected cookie string for all soundcloud.com cookies (valid after DialogResult=true).</summary>
    public string? SavedCookieHeader { get; private set; }

    public SoundCloudLoginWindow(SoundCloudService soundCloud, bool signOutFirst = false)
    {
        InitializeComponent();
        _soundCloud = soundCloud;
        _signOutFirst = signOutFirst;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _pollTimer?.Stop();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // WebView2 data folder lives in %LOCALAPPDATA%/BatPlayer: the exe folder may be read-only.
            var userDataFolder = System.IO.Path.Combine(App.AppDataDir, "webview2", "sc");
            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Profile folder locked by another instance or corrupted ("cache error") —
                // retry with a unique folder; the login runs fresh anyway.
                Logger.Warn($"WebView2 profile folder unavailable, using a fresh one: {ex.Message}");
                userDataFolder += "_" + Guid.NewGuid().ToString("N")[..8];
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            }
            await Web.EnsureCoreWebView2Async(env);

            // Profile cookies are NOT cleared: the persistent profile keeps the session, so a
            // regular "Connect" auto-logs back into the same account. "Switch account" first
            // opens the on-site logout so the user signs in with any account they choose.
            Web.Source = new Uri(_signOutFirst ? LogoutUrl : LoginUrl);

            // Poll cookies: login can complete without a navigation event (SPA redirects),
            // so the timer runs the whole time the window is open.
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += PollCookiesTick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            // Most common case: WebView2 Runtime not installed; the error text is localized.
            Logger.Error(ex, "SoundCloud login: WebView2 init failed");
            ShowWebviewMissing();
        }
    }

    private async void PollCookiesTick(object? sender, EventArgs e)
    {
        if (_finished || _checking || Web.CoreWebView2 == null) return;
        _checking = true;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(CookieDomain);
            if (cookies.All(c => c.Name != OAuthCookieName)) return;

            // First oauth_token sighting: the session is still being written — wait for stability.
            _oauthFirstSeenUtc ??= DateTime.UtcNow;
            var sinceFirst = DateTime.UtcNow - _oauthFirstSeenUtc.Value;
            var count = cookies.Count;
            var stable = count == _lastCookieCount;
            _lastCookieCount = count;
            if (sinceFirst < CookieStabilityDelay || (!stable && sinceFirst < CookieMaxWait))
                return;

            _finished = true;
            _pollTimer?.Stop();

            // Collect ALL soundcloud.com cookies into the cookie string for api-v2 requests.
            // (Cookie names are safe to log; values are never logged.)
            SavedCookieHeader = string.Join("; ",
                cookies.Select(c => $"{c.Name}={c.Value}"));
            Logger.Info($"SoundCloud login: saved {count} session cookies ({sinceFirst.TotalSeconds:0}s after oauth_token): " +
                        string.Join(", ", cookies.Select(c => c.Name).Distinct().OrderBy(n => n)));

            _soundCloud.SaveSessionCookies(SavedCookieHeader);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            // A single poll failure doesn't close the window — retry on the next tick.
            Logger.Error(ex, "SoundCloud login: cookie poll failed");
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowWebviewMissing()
    {
        Web.Visibility = Visibility.Collapsed;
        WebviewMissingPanel.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            try { DragMove(); } catch { /* window may already be closed */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
