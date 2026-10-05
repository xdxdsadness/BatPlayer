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
/// Окно входа в VK — авторизация COOKIES ВЕБ-СЕССИИ, единый принцип с SoundCloud-окном.
/// OAuth-фаза (oauth.vk.com/authorize + Kate Mobile) удалена: токены audio.get у VK
/// мертвы, каталог отдаётся веб-эндпоинтом al_audio.php по cookies сессии сайта.
///
/// Поток: открывается обычная мобильная страница входа https://m.vk.com/login (без экрана
/// стороннего приложения). Успех детектится опросом раз в 500 мс — навигация на feed/al_im
/// (домены vk.com И vk.ru) ИЛИ появление cookie веб-сессии remixsid (сайт мог сам
/// восстановить сессию без редиректа на feed). После детекта:
///   1) cookies собираются с обоих доменов (vk.ru + vk.com → "name=value; …");
///   2) userId берётся из cookie remixuid, иначе regex'ом по HTML текущей страницы
///      (ExecuteScriptAsync), иначе GET m.vk.ru/feed с cookies через HttpClient;
///   3) VkService.SaveSessionCookies(cookieHeader, userId), окно закрывается DialogResult=true.
/// Cookies в лог не пишутся (логируется только id пользователя и факт сохранения сессии).
/// WebView2 ходит НАПРЯМУЮ (--no-proxy-server): VK лимитирует попытки входа через VPN-выход.
/// Если WebView2 Runtime не установлен — локализованная ошибка, приложение не падает.
/// </summary>
    public partial class VkLoginWindow : Window
{
    private static readonly string[] CookieDomains = new[] { "https://vk.ru", "https://vk.com", "https://m.vk.com", "https://login.vk.ru" };

    /// <summary>Обычный вход на сайте VK (мобильная версия — надёжный редирект на feed).</summary>
    private const string LoginUrl = "https://vk.ru/login";

    /// <summary>Домены, cookies которых собираем (vk.ru — актуальный, vk.com — исторический).</summary>
    private const string CookieDomainRu = "https://vk.ru";
    private const string CookieDomainCom = "https://vk.com";

    /// <summary>Cookie авторизованной веб-сессии VK.</summary>
    private const string SessionCookieName = "remixsid";

    /// <summary>Cookie с id пользователя (значение — uid); приходит не всегда.</summary>
    private const string UserIdCookieName = "remixuid";

    /// <summary>Страница, с которой берём HTML для извлечения uid (и Referer запроса).</summary>
    private const string FeedUrl = "https://m.vk.ru/feed";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly VkService _vk;
    private DispatcherTimer? _pollTimer;
    private bool _checking;
    private bool _finished;
    private bool _ruWarmupDone;
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
            // Папка данных WebView2 — в %LOCALAPPDATA%/BatPlayer: exe-папка может быть read-only.
            var userDataFolder = System.IO.Path.Combine(App.AppDataDir, "webview2", "vk");
            // VK доступен в регионе пользователя напрямую, а через VPN-выход VK лимитирует
            // попытки входа ("Too many attempts" сразу после телефона) — ходим НАПРЯМУЮ.
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
                // Папка профиля занята другим экземпляром/повреждена («ошибка кеша») —
                // повторяем с уникальной папкой, вход всё равно будет выполнен заново.
                Logger.Warn($"WebView2 profile folder unavailable, using a fresh one: {ex.Message}");
                userDataFolder += "_" + Guid.NewGuid().ToString("N")[..8];
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder,
                    options: envOptions);
            }
            await Web.EnsureCoreWebView2Async(env);

            await ClearPlatformCookiesAsync(Web.CoreWebView2);

            Web.Source = new Uri(LoginUrl);

            // Опрос cookies/навигации: вход может завершиться без нужного нам события
            // (SPA-редиректы, восстановленная сессия) — таймер работает, пока окно открыто.
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += PollSessionTick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            // Самый частый случай — WebView2 Runtime не установлен; текст ошибки локализован.
            Logger.Error(ex, "VK login: WebView2 init failed");
            ShowWebviewMissing();
        }
    }

    /// <summary>
    /// Опрос: вход выполнен, если WebView ушёл на feed/al_im ИЛИ появилась cookie веб-сессии.
    /// Идемпотентно (флаг _finished), повторные тики после закрытия ничего не делают.
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

            // Прогрев аудио-раздела: после входа VK требует посещения vk.ru/audio —
            // без него al_audio.php отвечает кодом 3 (челлендж) вместо каталога.
            if (!_audioWarmupDone)
            {
                if (!IsLoggedIn(source, pairsRu, pairsCom)) return;
                _audioWarmupDone = true;
                Web.Source = new Uri("https://vk.ru/audio");
                return; // cookies соберутся после загрузки аудио-раздела
            }

            // Сохранение — когда прогрев завершён: любая страница vk.ru/vk.com,
            // КРОМЕ самой процедуры входа (login.*). После /audio VK может оставить
            // адрес как есть (SPA) — это тоже «доехали».
            var settled = source != null
                          && (source.Contains("vk.ru/", StringComparison.OrdinalIgnoreCase)
                              || source.Contains("vk.com/", StringComparison.OrdinalIgnoreCase))
                          && !source.Contains("login.", StringComparison.OrdinalIgnoreCase);
            if (!settled) return;

            var cookieHeader = BuildCookieHeader(pairsRu, pairsCom);
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return; // cookies ещё не устоялись — попробуем на следующем тике

            // Приоритетный источник id — адрес аудио-раздела vk.ru/audios{uid};
            // фолбэк — HTML текущей страницы (uid/viewer_id) и лента.
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
            // Ошибка одного опроса не закрывает окно — попробуем на следующем тике.
            Logger.Error(ex, "VK login: session poll failed");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Вход выполнен: feed/al_im на любом домене VK либо cookie веб-сессии.
    /// Чистая функция (пары name/value вместо CoreWebView2Cookie) — покрыта юнит-тестами.</summary>
    internal static bool IsLoggedIn(string? uri,
        IEnumerable<(string Name, string Value)> cookiesRu,
        IEnumerable<(string Name, string Value)> cookiesCom)
    {
        // VK использует домены vk.ru И vk.com (в т.ч. m.vk.*) — распознаём оба.
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
    /// Cookie-строка для vk.com/vk.ru: сначала все cookies vk.ru (актуальный домен),
    /// затем отсутствующие по имени cookies vk.com. Пустые имена/значения пропускаются.
    /// Чистая функция — покрыта юнит-тестами.
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
    /// id пользователя: cookie remixuid → HTML текущей страницы (ExecuteScriptAsync) →
    /// GET m.vk.ru/feed с cookies. null — не нашли (сессия сохранится без id, синк
    /// каталога потребует повторного входа); найденное значение логируется.
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

    /// <summary>HTML текущего документа WebView (ExecuteScriptAsync возвращает JSON-строку).</summary>
    private async Task<string?> ReadPageHtmlAsync()
    {
        try
        {
            var json = await Web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            // ExecuteScriptAsync отдаёт результат как JSON — разворачиваем в строку.
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (Exception ex)
        {
            // Страница могла ещё грузиться/упасть — это лишь один из источников id.
            Logger.Error(ex, "VK login: page html read failed");
            return null;
        }
    }

    /// <summary>GET m.vk.ru/feed с cookies сессии (тот же прямой слой, что у каталога).</summary>
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

    /// <summary>Сохранение сессии и закрытие окна (идемпотентно).</summary>
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
            try { DragMove(); } catch { /* окно могло закрыться */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    /// <summary>
    /// Очистка cookies платформы перед входом: после Disconnect постоянный профиль
    /// WebView2 держит старую сессию и автологин возвращал в тот же аккаунт.
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
