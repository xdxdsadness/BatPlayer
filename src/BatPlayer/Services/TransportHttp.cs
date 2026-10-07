using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services.SoundCloud;

namespace BatPlayer.Services;

/// <summary>
/// Shared HTTP transport for platform services (VK, Yandex Music): direct first, with a
/// single retry through the WinINET system proxy (including "socks=host:port" written by
/// VPN clients) on a network error. The working transport is cached with a TTL so requests
/// do not pay a double timeout every call. Clients are per-instance singletons, recreated
/// only when the system proxy changes. Access tokens never travel through this layer's
/// logging. Proxy registry/string parsing is reused from the SoundCloud layer.
/// </summary>
internal abstract class TransportHttp
{
    private const int TimeoutSeconds = 15;

    private static readonly TimeSpan WorkingModeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SystemProxyCacheTtl = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly string _logName;

    private HttpClient? _directClient;
    private HttpClient? _proxyClient;
    private string? _proxyClientKey;

    // Working-mode cache: true = proxy, false = direct; null/expired — probe again.
    private bool? _workingMode;
    private string? _workingModeProxyKey;
    private DateTime _workingModeUntilUtc = DateTime.MinValue;

    private (string Scheme, string Host, int Port)? _systemProxy;
    private DateTime _systemProxyReadUtc = DateTime.MinValue;

    protected TransportHttp(string logName) => _logName = logName;

    /// <summary>User-Agent of the owning service.</summary>
    protected abstract string UserAgent { get; }

    /// <summary>Service-specific default headers, added once per client.</summary>
    protected virtual void ConfigureDefaultHeaders(HttpClient client) { }

    /// <summary>
    /// GET with transport failover (direct -> system proxy). <paramref name="createRequest"/>
    /// is called per attempt (HttpRequestMessage cannot be reused). Returns (status, body);
    /// when every transport fails, the last network exception is rethrown.
    /// </summary>
    public async Task<(int Status, string Body)> SendWithFailoverAsync(
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
            Logger.Info($"{_logName} {TransportName(plan.FirstIsProxy, plan.Endpoint)} failed ({ex.GetType().Name}), " +
                        $"retrying via {TransportName(!plan.FirstIsProxy, plan.Endpoint)}");
            var result = await SendOnceAsync(plan.Fallback, createRequest, ct);
            CacheWorkingMode(!plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
    }

    /// <summary>
    /// GET with transport failover; the body is returned as a Stream (covers and mp3
    /// streams are downloaded to cache files without buffering in memory). The caller
    /// copies the stream and must dispose <paramref name="owner"/> (it closes the
    /// response/request). The status is not checked: non-2xx is returned as is with the
    /// error-body stream.
    /// </summary>
    public async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
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
            Logger.Info($"{_logName} {TransportName(plan.FirstIsProxy, plan.Endpoint)} failed ({ex.GetType().Name}), " +
                        $"retrying via {TransportName(!plan.FirstIsProxy, plan.Endpoint)}");
            var result = await SendStreamOnceAsync(plan.Fallback, createRequest, ct);
            CacheWorkingMode(!plan.FirstIsProxy, plan.Endpoint);
            return result;
        }
    }

    // ===================== Transport selection =====================

    /// <summary>Attempt plan: first transport + fallback (direct first, proxy as fallback).</summary>
    private sealed record TransportPlan(
        HttpClient First, HttpClient? Fallback, bool FirstIsProxy,
        (string Scheme, string Host, int Port)? Endpoint);

    private TransportPlan ResolveTransports()
    {
        var endpoint = ReadSystemProxyCached();

        if (PeekWorkingMode(endpoint) == true && endpoint != null)
            return new TransportPlan(GetProxyClient(endpoint.Value), GetDirectClient(), true, endpoint);

        return endpoint != null
            ? new TransportPlan(GetDirectClient(), GetProxyClient(endpoint.Value), false, endpoint)
            : new TransportPlan(GetDirectClient(), null, false, endpoint);
    }

    private (string Scheme, string Host, int Port)? ReadSystemProxyCached()
    {
        lock (_lock)
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

    // ==================== Working-mode cache ===================

    /// <summary>Working mode from the cache, or null when empty/expired/bound to another proxy.</summary>
    private bool? PeekWorkingMode((string Scheme, string Host, int Port)? endpoint)
    {
        lock (_lock)
        {
            if (_workingMode == null || DateTime.UtcNow >= _workingModeUntilUtc) return null;
            if (_workingMode == true &&
                (endpoint == null || _workingModeProxyKey != SoundCloudProxyConfig.Format(endpoint.Value)))
                return null; // system proxy changed — the cached "proxy" mode is stale
            return _workingMode;
        }
    }

    private void CacheWorkingMode(bool useProxy, (string Scheme, string Host, int Port)? endpoint)
    {
        var key = useProxy && endpoint != null ? SoundCloudProxyConfig.Format(endpoint.Value) : null;

        lock (_lock)
        {
            var unchanged = _workingMode == useProxy
                            && string.Equals(_workingModeProxyKey, key, StringComparison.Ordinal)
                            && DateTime.UtcNow < _workingModeUntilUtc;
            _workingMode = useProxy;
            _workingModeProxyKey = key;
            _workingModeUntilUtc = DateTime.UtcNow + WorkingModeTtl;

            if (unchanged) return; // TTL extended — mode did not change, do not log again
        }

        Logger.Info($"{_logName} network mode selected: {key ?? "direct"}");
    }

    // ======================== Clients ===========================

    private HttpClient GetDirectClient()
    {
        lock (_lock) return _directClient ??= CreateClient(proxy: null);
    }

    private HttpClient GetProxyClient((string Scheme, string Host, int Port) endpoint)
    {
        var key = SoundCloudProxyConfig.Format(endpoint);
        lock (_lock)
        {
            if (_proxyClient != null && string.Equals(_proxyClientKey, key, StringComparison.Ordinal))
                return _proxyClient;

            var webProxy = SoundCloudProxyConfig.TryCreateWebProxy(endpoint);
            if (webProxy == null)
                return _directClient ??= CreateClient(proxy: null); // unsupported scheme — degrade to direct

            // The old proxy client is not disposed: in-flight requests may use it.
            // Recreation happens only on proxy change, so sockets do not leak.
            _proxyClient = CreateClient(webProxy);
            _proxyClientKey = key;
        }

        Logger.Info($"{_logName} HTTP client created for proxy {key}"); // credentials are not part of the endpoint
        return _proxyClient;
    }

    private HttpClient CreateClient(WebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = proxy // null = direct; socks5:// and http:// are supported by SocketsHttpHandler
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        ConfigureDefaultHeaders(client);
        return client;
    }

    private async Task<(int Status, string Body)> SendOnceAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using var request = createRequest();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // The body is read as bytes and decoded manually: the charset header can be
        // malformed (e.g. doubled charsets), and ReadAsStringAsync would throw on it.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return ((int)response.StatusCode, DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet));
    }

    /// <summary>UTF-8 by default; windows-1251 when the bytes are invalid UTF-8.</summary>
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
                // unknown charset — fall through to detection below
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

    private async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamOnceAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var request = createRequest(); // no using: disposed by the owner together with the response
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return ((int)response.StatusCode, stream, new StreamOwner(response, request));
        }
        catch
        {
            // Headers/stream not received — clean up, the caller has nothing to own.
            request.Dispose();
            throw;
        }
    }

    /// <summary>Keeps the response and request alive while the caller reads the body stream.</summary>
    private sealed class StreamOwner(HttpResponseMessage response, HttpRequestMessage request) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose(); // also closes the Content stream
            request.Dispose();
        }
    }

    /// <summary>Network error worth retrying (unreachable/reset/timeout), not caller cancellation.</summary>
    private static bool IsRetryableNetworkError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is HttpRequestException
               || (ex is TaskCanceledException && ex.InnerException is TimeoutException); // HttpClient.Timeout fired
    }
}
