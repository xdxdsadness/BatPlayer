using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Единая фабрика HttpClient для SoundCloud-сервисов + цепочка фолбэков транспорта.
///
/// Авто-режим (настройка пуста): цепочка попыток
///   1) direct — напрямую;
///   2) локальный DPI-bypass (DpiBypassProxy, 127.0.0.1, фрагментация TLS ClientHello —
///      обход SNI-блокировок провайдера БЕЗ VPN и внешних серверов);
///   3) прокси пользователя/системы (AppSettings.SoundCloudProxy или WinINET-прокси,
///      который пишут VPN-клиенты) — если настроен.
/// Режимы "off" (только direct) и явный прокси из настройки оставляют один транспорт.
///
/// Успешный транспорт кэшируется (TTL ~10 мин) и становится первой попыткой, остальные
/// остаются фолбэками. Клиенты — статические синглтоны (не плодим сокеты), пересоздаются
/// только при смене прокси. Учётные данные прокси не используются и не логируются.
/// </summary>
internal static class SoundCloudHttp
{
    /// <summary>Таймаут одного запроса (как было до правки).</summary>
    private const int TimeoutSeconds = 15;

    /// <summary>Таймаут чтения тела медиа-ответа: пауза между сегментами больше этого
    /// значения трактуется как обрыв потока (DPI/CDN) и приводит к ретраю цепочки.</summary>
    private static readonly TimeSpan StreamReadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Сколько держим «рабочий режим» до повторного зондирования direct→proxy.</summary>
    private static readonly TimeSpan WorkingModeTtl = TimeSpan.FromMinutes(10);

    /// <summary>Реестр читаем не чаще раза в минуту.</summary>
    private static readonly TimeSpan SystemProxyCacheTtl = TimeSpan.FromMinutes(1);

    private static readonly object Lock = new();

    private static HttpClient? _directClient;
    private static HttpClient? _proxyClient;
    private static string? _proxyClientKey;
    private static HttpClient? _bypassClient;

    /// <summary>Антиблокировка zapret разрешена настройкой (AppSettings.SoundCloudZapretEnabled).
    /// Устанавливается SoundCloudService при старте и на смене настроек.</summary>
    public static bool ZapretEnabled { get; set; } = true;

    /// <summary>Нормализованное значение AppSettings.SoundCloudProxy, применённое к клиентам.</summary>
    private static string _appliedSetting = string.Empty;

    // Кэш рабочего режима: ключ транспорта ("direct"/"dpi-bypass"/формат прокси).
    // null/просрочен — зондируем заново.
    private const string DirectKey = "direct";
    private const string BypassKey = "dpi-bypass (local)";
    private static string? _workingModeKey;
    private static DateTime _workingModeUntilUtc = DateTime.MinValue;

    // Кэш ОТКАЗОВ: транспорт, только что упавший по сети, пропускается 90 секунд —
    // клики не должны каждый раз платить таймаутом мёртвого транспорта.
    private static readonly TimeSpan TransportFailTtl = TimeSpan.FromSeconds(90);
    private static readonly Dictionary<string, DateTime> TransportFailedUntil = new();

    private static bool IsTransportFailed(string key)
    {
        lock (Lock)
            return TransportFailedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until;
    }

    private static void MarkTransportFailed(string key)
    {
        lock (Lock) TransportFailedUntil[key] = DateTime.UtcNow + TransportFailTtl;
    }

    private static void MarkTransportAlive(string key)
    {
        lock (Lock) TransportFailedUntil.Remove(key);
    }

    private static (string Scheme, string Host, int Port)? _systemProxy;
    private static DateTime _systemProxyReadUtc = DateTime.MinValue;

    private enum ProxyMode
    {
        /// <summary>Пустая настройка: direct первым, системный прокси — фолбэк.</summary>
        Auto,
        /// <summary>"off": только direct, без фолбэка.</summary>
        Off,
        /// <summary>Явный прокси из настроек: только он, без фолбэка.</summary>
        Explicit
    }

    /// <summary>
    /// Применить настройку SoundCloudProxy: при старте приложения и на каждое изменение настроек.
    /// Смена значения сбрасывает кэш рабочего режима (следующий запрос зондирует заново).
    /// </summary>
    public static void ApplyUserSetting(string? setting)
    {
        var normalized = (setting ?? string.Empty).Trim();
        lock (Lock)
        {
            if (string.Equals(_appliedSetting, normalized, StringComparison.Ordinal)) return;
            _appliedSetting = normalized;
            InvalidateWorkingMode();
            _systemProxyReadUtc = DateTime.MinValue; // перечитать реестр на новом режиме
        }

        // В лог — только классификация; сама строка настройки не печатается (в ней могут быть credentials).
        Logger.Info($"SoundCloud proxy setting: {DescribeSetting(normalized)}");
    }

    /// <summary>
    /// GET с авто-выбором транспорта. <paramref name="createRequest"/> вызывается на каждую попытку
    /// (HttpRequestMessage переиспользовать нельзя). Возвращает (HTTP-код, тело); если не сработал
    /// ни один транспорт — пробрасывает последнее сетевое исключение.
    /// </summary>
    public static async Task<(int Status, string Body)> SendWithFailoverAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        // Второй проход — после запуска пакетной антиблокировки (winws): она правит
        // пакеты на уровне драйвера, direct-транспорт после её старта проходит сам.
        List<(HttpClient Client, string Name)> attempts;
        var zapretAttempted = false;
        while (true)
        {
            attempts = ResolveTransports();
            var failed = false;
            for (var i = 0; i < attempts.Count; i++)
            {
                var (client, name) = attempts[i];
                try
                {
                    var result = await SendOnceAsync(client, createRequest, ct);
                    CacheWorkingMode(name);
                    MarkTransportAlive(name);
                    return result;
                }
                catch (Exception ex) when (i < attempts.Count - 1 && IsRetryableNetworkError(ex, ct))
                {
                    MarkTransportFailed(name);
                    Logger.Info($"SoundCloud transport {name} failed ({ex.GetType().Name}: {RootMessage(ex)}), " +
                                $"retrying via {attempts[i + 1].Name}");
                }
                catch (Exception) when (i == attempts.Count - 1)
                {
                    MarkTransportFailed(name);
                    failed = true; // последний транспорт тоже упал — выход из прохода
                }
            }

            if (!failed || zapretAttempted
                || !await SoundCloudZapret.EnsureStartedAsync(ZapretEnabled).ConfigureAwait(false))
                break;
            zapretAttempted = true;
            Logger.Info("SoundCloud zapret started — retrying transports through it");
        }

        throw new HttpRequestException("all SoundCloud transports failed (direct, dpi-bypass, proxy, zapret)");
    }

    /// <summary>
    /// GET с авто-выбором транспорта, тело ответа отдаётся как Stream (для скачивания
    /// mp3-стримов в кэш-файл без буферизации в память). ResponseHeadersRead: тело НЕ читается
    /// в память здесь — вызывающий копирует Stream и обязан Dispose-нуть <paramref name="owner"/>
    /// (он закрывает response/request и соединение). Статус НЕ проверяется: не-2xx отдаётся
    /// как есть со стримом тела ошибки. Сетевое исключение последней попытки — пробрасывается.
    /// </summary>
    public static async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        // Второй проход — после запуска пакетной антиблокировки (winws): она правит
        // пакеты на уровне драйвера, direct-транспорт после её старта проходит сам.
        List<(HttpClient Client, string Name)> attempts;
        var zapretAttempted = false;
        while (true)
        {
            attempts = ResolveTransports();
            var failed = false;
            for (var i = 0; i < attempts.Count; i++)
            {
                var (client, name) = attempts[i];
                try
                {
                    var result = await SendStreamOnceAsync(client, createRequest, ct);
                    CacheWorkingMode(name);
                    MarkTransportAlive(name);
                    return result;
                }
                catch (Exception ex) when (i < attempts.Count - 1 && IsRetryableNetworkError(ex, ct))
                {
                    MarkTransportFailed(name);
                    Logger.Info($"SoundCloud transport {name} failed ({ex.GetType().Name}: {RootMessage(ex)}), " +
                                $"retrying via {attempts[i + 1].Name}");
                }
                catch (Exception) when (i == attempts.Count - 1)
                {
                    MarkTransportFailed(name);
                    failed = true; // последний транспорт тоже упал — выход из прохода
                }
            }

            if (!failed || zapretAttempted
                || !await SoundCloudZapret.EnsureStartedAsync(ZapretEnabled).ConfigureAwait(false))
                break;
            zapretAttempted = true;
            Logger.Info("SoundCloud zapret started — retrying transports through it");
        }

        throw new HttpRequestException("all SoundCloud transports failed (direct, dpi-bypass, proxy, zapret)");
    }

    // ===================== Выбор транспорта =====================

    /// <summary>Цепочка попыток транспорта в порядке приоритета (см. доку класса).</summary>
    private static List<(HttpClient Client, string Name)> ResolveTransports()
    {
        var (mode, endpoint) = Resolve();
        var direct = (GetDirectClient(), DirectKey);

        switch (mode)
        {
            case ProxyMode.Off:
                return new List<(HttpClient, string)> { direct };

            case ProxyMode.Explicit:
                // Явный выбор пользователя: direct не пробуем, даже если он «работает».
                return endpoint != null
                    ? new List<(HttpClient, string)> { (GetProxyClient(endpoint.Value), SoundCloudProxyConfig.Format(endpoint.Value)) }
                    : new List<(HttpClient, string)> { direct };

            default: // Auto
            {
                // Порядок: закэшированный рабочий транспорт первым, остальные — фолбэками.
                var bypassEndpoint = DpiBypassProxy.EnsureStarted();
                var bypass = bypassEndpoint != null
                    ? (GetBypassClient(bypassEndpoint.Value), BypassKey)
                    : ((HttpClient, string)?)null;
                var user = endpoint != null
                    ? ((HttpClient, string)?)(GetProxyClient(endpoint.Value), SoundCloudProxyConfig.Format(endpoint.Value))
                    : null;

                var cached = PeekWorkingMode();
                var ordered = new List<((HttpClient Client, string Name) T, string Key)>();
                void Add((HttpClient Client, string Name)? t, string? key)
                {
                    if (t == null || ordered.Any(o => o.T.Name == t.Value.Name)) return;
                    ordered.Add((t.Value, key ?? DirectKey));
                }

                if (cached == DirectKey) { Add(direct, DirectKey); Add(bypass, BypassKey); Add(user, endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null); }
                else if (cached == BypassKey) { Add(bypass, BypassKey); Add(direct, DirectKey); Add(user, endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null); }
                else if (cached != null) { Add(user, endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null); Add(bypass, BypassKey); Add(direct, DirectKey); }
                else { Add(direct, DirectKey); Add(bypass, BypassKey); Add(user, endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null); }

                // Упавшие 90 секунд назад транспорты — в конец цепочки (первую попытку
                // оставляем всегда: цепочка не должна остаться пустой).
                var attempts = ordered
                    .OrderBy(o => IsTransportFailed(o.Key) ? 1 : 0)
                    .Select(o => o.T)
                    .ToList();
                return attempts;
            }
        }
    }

    private static (ProxyMode Mode, (string Scheme, string Host, int Port)? Endpoint) Resolve()
    {
        string setting;
        lock (Lock) setting = _appliedSetting;

        if (setting.Length > 0)
        {
            if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
                return (ProxyMode.Off, null);

            var userEndpoint = SoundCloudProxyConfig.ParseUserProxy(setting);
            if (userEndpoint != null) return (ProxyMode.Explicit, userEndpoint);

            // Неразбираемая настройка уже залогирована в ApplyUserSetting — работаем как auto.
        }

        lock (Lock)
        {
            if (DateTime.UtcNow - _systemProxyReadUtc > SystemProxyCacheTtl)
            {
                _systemProxy = SoundCloudProxyConfig.ReadSystemProxy();
                _systemProxyReadUtc = DateTime.UtcNow;
            }
            return (ProxyMode.Auto, _systemProxy);
        }
    }

    private static string DescribeSetting(string normalized)
    {
        if (normalized.Length == 0) return "auto";
        if (string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase)) return "off";

        var parsed = SoundCloudProxyConfig.ParseUserProxy(normalized);
        return parsed != null ? SoundCloudProxyConfig.Format(parsed.Value) : "unrecognized, falling back to auto";
    }

    // ==================== Кэш рабочего режима ===================

    /// <summary>Рабочий транспорт из кэша или null, если кэш пуст/просрочен.</summary>
    private static string? PeekWorkingMode()
    {
        lock (Lock)
        {
            if (_workingModeKey == null || DateTime.UtcNow >= _workingModeUntilUtc) return null;
            return _workingModeKey;
        }
    }

    private static void CacheWorkingMode(string key)
    {
        lock (Lock)
        {
            var unchanged = string.Equals(_workingModeKey, key, StringComparison.Ordinal)
                            && DateTime.UtcNow < _workingModeUntilUtc;
            _workingModeKey = key;
            _workingModeUntilUtc = DateTime.UtcNow + WorkingModeTtl;

            if (unchanged) return; // продлили TTL — режим не менялся, не логируем повторно
        }

        Logger.Info($"SoundCloud network mode selected: {key}");
    }

    /// <summary>Сброс кэша выбора. Вызывается под <see cref="Lock"/>.</summary>
    private static void InvalidateWorkingMode()
    {
        _workingModeKey = null;
        _workingModeUntilUtc = DateTime.MinValue;
    }

    // ======================== Клиенты ===========================

    private static HttpClient GetDirectClient()
    {
        lock (Lock) return _directClient ??= CreateClient(proxy: null);
    }

    /// <summary>Клиент через локальный DPI-bypass прокси (фрагментация ClientHello).</summary>
    private static HttpClient GetBypassClient((string Host, int Port) endpoint)
    {
        lock (Lock)
        {
            if (_bypassClient != null) return _bypassClient;
            var webProxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}");
            _bypassClient = CreateClient(webProxy);
        }

        Logger.Info("SoundCloud HTTP client created for local DPI-bypass proxy");
        return _bypassClient;
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

        Logger.Info($"SoundCloud HTTP client created for proxy {key}"); // credentials в эндпоинт не входят
        return _proxyClient;
    }

    private static HttpClient CreateClient(WebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, // cookies SoundCloud передаются заголовком Cookie
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = proxy // null — direct; socks5:///http:// поддержаны SocketsHttpHandler
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(SoundCloudService.UserAgent);
        return client;
    }

    private static async Task<(int Status, string Body)> SendOnceAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using var request = createRequest();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, body);
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
            // Тело под таймаутом чтения: DPI/CDN обрыв потока посреди скачивания
            // превращается в обычную сетевую ошибку вместо вечного зависания.
            return ((int)response.StatusCode, new TimeoutReadStream(stream, StreamReadTimeout),
                    new StreamOwner(response, request));
        }
        catch
        {
            // Заголовки/стрим не получены — чистим за собой, вызывающему нечем владеть.
            request.Dispose();
            throw;
        }
    }

    /// <summary>Самый внутренний текст исключения: там живёт настоящая причина
    /// (DNS/timeout/refused/reset) — без него в логе только тип обёртки.</summary>
    private static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException != null) cur = cur.InnerException;
        return cur.Message;
    }

    /// <summary>
    /// Обёртка тела медиа-ответа: если данные перестают поступать на N секунд
    /// (DPI рвёт поток посреди скачивания, обрыв CDN) — IOException вместо вечного
    /// зависания. Вызывающий код ловит его как обычный сетевой сбой (ретрай/фолбэк).
    /// </summary>
    internal sealed class TimeoutReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _readTimeout;

        public TimeoutReadStream(Stream inner, TimeSpan readTimeout)
        {
            _inner = inner;
            _readTimeout = readTimeout;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_readTimeout);
            try
            {
                return await _inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("media stream stalled (no data within read timeout)");
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
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
