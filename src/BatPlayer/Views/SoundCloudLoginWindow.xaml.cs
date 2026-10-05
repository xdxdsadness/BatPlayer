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
/// Окно входа в SoundCloud: встроенный WebView2 открывает soundcloud.com/login.
/// Успех детектится по появлению cookie oauth_token для soundcloud.com (опрос 500мс);
/// после этого все cookies soundcloud.com собираются в Cookie-строку и отдаются сервису
/// (сохранение в sc_auth.json — не здесь, а в SoundCloudLoginService).
/// Если WebView2 Runtime не установлен — показывается понятная ошибка, приложение не падает.
/// </summary>
    public partial class SoundCloudLoginWindow : Window
{
    /// <summary>Адрес страницы входа.</summary>
    private const string LoginUrl = "https://soundcloud.com/login";
    /// <summary>Выход из текущей сессии НА САЙТЕ: открывает штатные страницы SoundCloud,
    /// где пользователь сам выбирает, каким аккаунтом войти (сайт помнит почту, предлагает
    /// продолжить под текущим и т.п.) — как в аккаунт-чузере Google, но средствами сайта.</summary>
    private const string LogoutUrl = "https://soundcloud.com/logout";
    /// <summary>Сайт, cookies которого собираем.</summary>
    private const string CookieDomain = "https://soundcloud.com";
    /// <summary>Имя cookie, появление которой означает успешный вход.</summary>
    private const string OAuthCookieName = "oauth_token";

    private readonly SoundCloudService _soundCloud;
    /// <summary>Сначала выйти из текущей сессии на сайте (режим «Сменить аккаунт»).</summary>
    private readonly bool _signOutFirst;
    private DispatcherTimer? _pollTimer;
    private bool _checking;
    private bool _finished;

    // oauth_token появляется РАНЬШЕ, чем веб-логин дописывает остальную сессию
    // (sc_session, свежий datadome). Сохранить всё сразу — получить неполный набор:
    // media MONETIZE-треков требует именно полной сессии. Ждём после первого
    // появления oauth_token, пока набор cookies стабилизируется.
    private DateTime? _oauthFirstSeenUtc;
    private int _lastCookieCount = -1;

    /// <summary>Сколько ждать после появления oauth_token, пока cookies перестанут меняться.</summary>
    private static readonly TimeSpan CookieStabilityDelay = TimeSpan.FromSeconds(3);
    /// <summary>Верхняя граница ожидания: сохраняем и «нестабильный» набор, чтобы вход не зависал.</summary>
    private static readonly TimeSpan CookieMaxWait = TimeSpan.FromSeconds(8);

    /// <summary>Собранная Cookie-строка всех cookies soundcloud.com (валидна после DialogResult=true).</summary>
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
            // Папка данных WebView2 — в %LOCALAPPDATA%/BatPlayer: exe-папка может быть read-only.
            var userDataFolder = System.IO.Path.Combine(App.AppDataDir, "webview2", "sc");
            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Папка профиля занята другим экземпляром/повреждена («ошибка кеша») —
                // повторяем с уникальной папкой, вход всё равно будет выполнен заново.
                Logger.Warn($"WebView2 profile folder unavailable, using a fresh one: {ex.Message}");
                userDataFolder += "_" + Guid.NewGuid().ToString("N")[..8];
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            }
            await Web.EnsureCoreWebView2Async(env);

            // Cookies профиля НЕ чистим: постоянный профиль хранит сессию — при обычном
            // «Подключить» автологин возвращает в тот же аккаунт без ввода пароля.
            // «Сменить аккаунт» сначала открывает штатный logout НА САЙТЕ: пользователь
            // видит настоящие страницы SoundCloud и входит каким хочет аккаунтом.
            Web.Source = new Uri(_signOutFirst ? LogoutUrl : LoginUrl);

            // Опрос cookies: вход может завершиться без события навигации (SPA-редиректы),
            // поэтому таймер работает всё время, пока окно открыто.
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += PollCookiesTick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            // Самый частый случай — WebView2 Runtime не установлен; текст ошибки локализован.
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

            // Первое появление oauth_token: сессия ещё дописывается — ждём стабилизации.
            _oauthFirstSeenUtc ??= DateTime.UtcNow;
            var sinceFirst = DateTime.UtcNow - _oauthFirstSeenUtc.Value;
            var count = cookies.Count;
            var stable = count == _lastCookieCount;
            _lastCookieCount = count;
            if (sinceFirst < CookieStabilityDelay || (!stable && sinceFirst < CookieMaxWait))
                return;

            _finished = true;
            _pollTimer?.Stop();

            // Собираем ВСЕ cookies soundcloud.com — Cookie-строку для api-v2 запросов.
            // (Имена cookies в логе безопасны, значения не логируем.)
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
            // Ошибка одного опроса не закрывает окно — попробуем на следующем тике.
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
            try { DragMove(); } catch { /* окно могло закрыться */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
