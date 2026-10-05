using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services.SoundCloud;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Единая фабрика HttpClient для Яндекс Музыки + фолбэк direct → системный прокси.
///
/// Яндекс Музыка доступна в регионе пользователя напрямую; VPN-выход наоборот часто
/// ломает авторизацию (Яндекс режет подозрительные сессии). Поэтому режим всегда
/// «direct первым», а системный прокси WinINET (в т.ч. "socks=host:port" от VPN-клиентов)
/// используется как фолбэк при сетевой ошибке — по образцу <see cref="VkHttp"/>.
///
/// Успешный транспорт кэшируется (TTL), чтобы не платить двойным таймаутом на каждом
/// запросе. Клиенты — статические синглтоны (не плодим сокеты), пересоздаются только
/// при смене системного прокси.
///
/// Заголовки — как у веб-клиента Яндекс Музыки: авторизация идёт OAuth-токеном Яндекс ID
/// (заголовок "Authorization: OAuth …" ставит вызывающий код), а
/// <c>X-Yandex-Music-Client: web</c> просит api.music.yandex.net отдавать веб-раскладку
/// JSON. Токены в логи не попадают. Разбор реестра/строки прокси переиспользуется из
/// SC-слоя (чистые парсеры).
/// </summary>
internal static class YmHttp
{
    /// <summary>Таймаут одного запроса.</summary>
    private const int TimeoutSeconds = 15;

    /// <summary>Сколько держим «рабочий режим» до повторного зондирования direct→proxy.</summary>
    private static readonly TimeSpan WorkingModeTtl = TimeSpan.FromMinutes(10);

    /// <summary>Реестр читаем не чаще раза в минуту.</summary>
    private static readonly TimeSpan SystemProxyCacheTtl = TimeSpan.FromMinutes(1);

    private static readonly object Lock = new();

    private static HttpClient? _directClient;
    private static HttpClient? _proxyClient;
    private static string? _proxyClientKey;

    // Кэш рабочего режима: true — прокси, false — direct; null/просрочен — зондируем заново.
    private static bool? _workingMode;
    private static string? _workingModeProxyKey;
    private static DateTime _workingModeUntilUtc = DateTime.MinValue;

    private static (string Scheme, string Host, int Port)? _systemProxy;
    private static DateTime _systemProxyReadUtc = DateTime.MinValue;

    /// <summary>
    /// GET с авто-выбором транспорта (direct → системный прокси).
    /// <paramref name="createRequest"/> вызывается на каждую попытку (HttpRequestMessage
    /// переиспользовать нельзя). Возвращает (HTTP-код, тело); если не сработал ни один
    /// транспорт — пробрасывает последнее сетевое исключение.
    /// </summary>
    public static async Task<(int Status, string Body)> SendWithFailoverAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var plan = ResolveTransports();

        try
        {
            var result = await SendOnceAsync(plan.First, createRequest, ct);
            CacheWorkingMode(plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
        catch (Exception ex) when (plan.Fallback != null && IsRetryableNetworkError(ex, ct))
        {
            Logger.Info($"Yandex Music {TransportName(plan.FirstIsProxy, plan.Endpoint)} failed ({ex.GetType().Name}), " +
                        $"retrying via {TransportName(!plan.FirstIsProxy, plan.Endpoint)}");
            var result = await SendOnceAsync(plan.Fallback, createRequest, ct);
            CacheWorkingMode(!plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
    }

    /// <summary>
    /// GET с авто-выбором транспорта, тело ответа отдаётся как Stream (скачивание обложек
    /// и mp3-стримов в кэш-файл без буферизации в память). ResponseHeadersRead: тело НЕ
    /// читается в память здесь — вызывающий копирует Stream и обязан Dispose-нуть
    /// <paramref name="owner"/> (он закрывает response/request и соединение). Статус НЕ
    /// проверяется: не-2xx отдаётся как есть со стримом тела ошибки.
    /// </summary>
    public static async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var plan = ResolveTransports();

        try
        {
            var result = await SendStreamOnceAsync(plan.First, createRequest, ct);
            CacheWorkingMode(plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
        catch (Exception ex) when (plan.Fallback != null && IsRetryableNetworkError(ex, ct))
        {
            Logger.Info($"Yandex Music {TransportName(plan.FirstIsProxy, plan.Endpoint)} failed ({ex.GetType().Name}), " +
                        $"retrying via {TransportName(!plan.FirstIsProxy, plan.Endpoint)}");
            var result = await SendStreamOnceAsync(plan.Fallback, createRequest, ct);
            CacheWorkingMode(!plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
    }

    // ===================== Выбор транспорта =====================

    /// <summary>План попыток: первый транспорт + фолбэк (direct первым, прокси — фолбэк).</summary>
    private sealed record TransportPlan(
        HttpClient First, HttpClient? Fallback, bool FirstIsProxy,
        (string Scheme, string Host, int Port)? Endpoint);

    private static TransportPlan ResolveTransports()
    {
        var endpoint = ReadSystemProxyCached();

        if (PeekWorkingMode(endpoint) == true && endpoint != null)
            return new TransportPlan(GetProxyClient(endpoint.Value), GetDirectClient(), true, endpoint);

        return endpoint != null
            ? new TransportPlan(GetDirectClient(), GetProxyClient(endpoint.Value), false, endpoint)
            : new TransportPlan(GetDirectClient(), null, false, endpoint);
    }

    private static (string Scheme, string Host, int Port)? ReadSystemProxyCached()
    {
        lock (Lock)
        {
            if (DateTime.UtcNow - _systemProxyReadUtc > SystemProxyCacheTtl)
            {
                _systemProxy = SoundCloudProxyConfig.ReadSystemProxy();
                _systemProxyReadUtc = DateTime.UtcNow;
            }
            return _systemProxy;
        }
    }

    private static string TransportName(bool isProxy, (string Scheme, string Host, int Port)? endpoint)
        => isProxy && endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : "direct";

    // ==================== Кэш рабочего режима ===================

    /// <summary>
    /// Рабочий режим из кэша или null, если кэш пуст/просрочен/относится к другому прокси.
    /// </summary>
    private static bool? PeekWorkingMode((string Scheme, string Host, int Port)? endpoint)
    {
        lock (Lock)
        {
            if (_workingMode == null || DateTime.UtcNow >= _workingModeUntilUtc) return null;
            if (_workingMode == true &&
                (endpoint == null || _workingModeProxyKey != SoundCloudProxyConfig.Format(endpoint.Value)))
                return null; // системный прокси сменился — кэш «прокси» больше не про этот эндпоинт
            return _workingMode;
        }
    }

    private static void CacheWorkingMode(bool useProxy, (string Scheme, string Host, int Port)? endpoint)
    {
        var key = useProxy && endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null;

        lock (Lock)
        {
            var unchanged = _workingMode == useProxy
                            && string.Equals(_workingModeProxyKey, key, StringComparison.Ordinal)
                            && DateTime.UtcNow < _workingModeUntilUtc;
            _workingMode = useProxy;
            _workingModeProxyKey = key;
            _workingModeUntilUtc = DateTime.UtcNow + WorkingModeTtl;

            if (unchanged) return; // продлили TTL — режим не менялся, не логируем повторно
        }

        Logger.Info($"Yandex Music network mode selected: {key ?? "direct"}");
    }

    // ======================== Клиенты ===========================

    private static HttpClient GetDirectClient()
    {
        lock (Lock) return _directClient ??= CreateClient(proxy: null);
    }

    private static HttpClient GetProxyClient((string Scheme, string Host, int Port) endpoint)
    {
        var key = SoundCloudProxyConfig.Format(endpoint);
        lock (Lock)
        {
            if (_proxyClient != null && string.Equals(_proxyClientKey, key, StringComparison.Ordinal))
                return _proxyClient;

            var webProxy = SoundCloudProxyConfig.TryCreateWebProxy(endpoint);
            if (webProxy == null)
                return _directClient ??= CreateClient(proxy: null); // схема не поддержана — деградируем в direct

            // Старый прокси-клиент не Dispose'им: по нему могут идти in-flight запросы.
            // Пересоздание происходит только при смене прокси, так что сокеты не утекают.
            _proxyClient = CreateClient(webProxy);
            _proxyClientKey = key;
        }

        Logger.Info($"Yandex Music HTTP client created for proxy {key}"); // credentials в эндпоинт не входят
        return _proxyClient;
    }

    private static HttpClient CreateClient(WebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, // авторизация — OAuth-заголовком от вызывающего кода, cookie-jar не нужен
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = proxy // null — direct; socks5:///http:// поддержаны SocketsHttpHandler
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(YmService.UserAgent);
        // Заголовки веб-клиента Яндекс Музыки: API отвечает JSON-раскладкой для веба.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Yandex-Music-Client", YmService.MusicClientHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Yandex-Music-Client", YmService.MusicClientHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru");
        return client;
    }

    private static async Task<(int Status, string Body)> SendOnceAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using var request = createRequest();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Тело читаем байтами и декодируем сами: charset в заголовке бывает кривым,
        // а JSON всегда UTF-8.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return ((int)response.StatusCode, DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet));
    }

    /// <summary>Декодирование тела ответа: UTF-8 по умолчанию, windows-1251 — если UTF-8 невалиден.</summary>
    private static string DecodeBody(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0) return string.Empty;

        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                var cs = charset.Trim().Trim('"').Split(';')[0].Trim();
                return System.Text.Encoding.GetEncoding(cs).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // неизвестный charset — падаем в автоопределение ниже
            }
        }

        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamOnceAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var request = createRequest(); // НЕ using: закрывается владельцем вместе с response
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return ((int)response.StatusCode, stream, new StreamOwner(response, request));
        }
        catch
        {
            // Заголовки/стрим не получены — чистим за собой, вызывающему нечем владеть.
            request.Dispose();
            throw;
        }
    }

    /// <summary>Держит response и request живыми, пока вызывающий читает Stream тела.</summary>
    private sealed class StreamOwner(HttpResponseMessage response, HttpRequestMessage request) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose(); // закрывает и Content-стрим
            request.Dispose();
        }
    }

    /// <summary>
    /// Сетевая ли ошибка (ретрай имеет смысл): недоступность/обрыв/таймаут HTTP-запроса,
    /// но НЕ отмена вызывающим кодом.
    /// </summary>
    private static bool IsRetryableNetworkError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is HttpRequestException
               || (ex is TaskCanceledException && ex.InnerException is TimeoutException); // сработал HttpClient.Timeout
    }
}
