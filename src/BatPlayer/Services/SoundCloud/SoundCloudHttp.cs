using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Shared HttpClient factory for SoundCloud services + transport fallback chain.
///
/// Auto mode (empty setting), attempt order:
///   1) direct;
///   2) local DPI-bypass (DpiBypassProxy, 127.0.0.1, TLS ClientHello fragmentation —
///      bypasses provider SNI blocking without a VPN or external servers);
///   3) user/system proxy (AppSettings.SoundCloudProxy, or the WinINET proxy written by
///      VPN clients) — if configured.
/// "off" mode (direct only) and an explicit proxy leave a single transport.
///
/// The working transport is cached (TTL ~10 min) and tried first; the rest stay as fallbacks.
/// Clients are static singletons (no socket churn) and are recreated only when the proxy
/// changes. Proxy credentials are never used or logged.
/// </summary>
internal static class SoundCloudHttp
{
    /// <summary>Per-request timeout.</summary>
    private const int TimeoutSeconds = 15;

    /// <summary>Media body read timeout: a pause between segments longer than this is
    /// treated as a stream drop (DPI/CDN) and retried through the transport chain.</summary>
    private static readonly TimeSpan StreamReadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long the working mode is trusted before re-probing direct→proxy.</summary>
    private static readonly TimeSpan WorkingModeTtl = TimeSpan.FromMinutes(10);

    /// <summary>Registry read at most once a minute.</summary>
    private static readonly TimeSpan SystemProxyCacheTtl = TimeSpan.FromMinutes(1);

    private static readonly object Lock = new();

    private static HttpClient? _directClient;
    private static HttpClient? _proxyClient;
    private static string? _proxyClientKey;
    private static HttpClient? _bypassClient;

    /// <summary>Packet-level bypass allowed by setting (AppSettings.DpiBypassEnabled).
    /// Set by SoundCloudService at startup and on setting changes.</summary>
    public static bool DpiBypassEnabled { get; set; } = true;

    /// <summary>Normalized AppSettings.SoundCloudProxy value applied to the clients.</summary>
    private static string _appliedSetting = string.Empty;

    // Working-mode cache: transport key ("direct"/"dpi-bypass"/proxy format).
    // null/expired — probe again.
    private const string DirectKey = "direct";
    private const string BypassKey = "dpi-bypass (local)";
    private static string? _workingModeKey;
    private static DateTime _workingModeUntilUtc = DateTime.MinValue;

    // Failure cache: a transport that just failed on the network is skipped for 90 seconds —
    // clicks must not pay the dead transport's timeout every time.
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
        /// <summary>Empty setting: direct first, system proxy as fallback.</summary>
        Auto,
        /// <summary>"off": direct only, no fallback.</summary>
        Off,
        /// <summary>Explicit proxy from settings: only it, no fallback.</summary>
        Explicit
    }

    /// <summary>
    /// Applies the SoundCloudProxy setting at app start and on every settings change.
    /// A value change resets the working-mode cache (the next request re-probes).
    /// </summary>
    public static void ApplyUserSetting(string? setting)
    {
        var normalized = (setting ?? string.Empty).Trim();
        lock (Lock)
        {
            if (string.Equals(_appliedSetting, normalized, StringComparison.Ordinal)) return;
            _appliedSetting = normalized;
            InvalidateWorkingMode();
            _systemProxyReadUtc = DateTime.MinValue; // re-read the registry for the new mode
        }

        // Log only the classification; the setting string itself is not printed (may contain credentials).
        Logger.Info($"SoundCloud proxy setting: {DescribeSetting(normalized)}");
    }

    /// <summary>
    /// GET with automatic transport selection. <paramref name="createRequest"/> is invoked on
    /// every attempt (HttpRequestMessage cannot be reused). Returns (HTTP status, body); if no
    /// transport works, rethrows the last network exception.
    /// </summary>
    public static async Task<(int Status, string Body)> SendWithFailoverAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        // Second pass after the packet-level bypass starts: it rewrites packets at the
        // driver level, so the direct transport passes on its own afterwards.
        List<(HttpClient Client, string Name)> attempts;
        var bypassAttempted = false;
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
                    failed = true; // last transport failed too — end the pass
                }
            }

            if (!failed || bypassAttempted
                || !await DpiBypass.EnsureStartedAsync(DpiBypassEnabled).ConfigureAwait(false))
                break;
            bypassAttempted = true;
            Logger.Info("SoundCloud packet bypass started — retrying transports through it");
        }

        throw new HttpRequestException("all SoundCloud transports failed (direct, dpi-bypass, proxy, packet bypass)");
    }

    /// <summary>
    /// GET with automatic transport selection, response body returned as a Stream (for
    /// downloading media streams to a cache file without memory buffering). The body is NOT
    /// read into memory here — the caller copies the Stream and must Dispose <paramref name="owner"/>
    /// (it closes response/request and the connection). Status is NOT checked: non-2xx is
    /// returned as-is with the error body stream. The last attempt's network exception is rethrown.
    /// </summary>
    public static async Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        // Second pass after the packet-level bypass starts: it rewrites packets at the
        // driver level, so the direct transport passes on its own afterwards.
        List<(HttpClient Client, string Name)> attempts;
        var bypassAttempted = false;
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
                    failed = true; // last transport failed too — end the pass
                }
            }

            if (!failed || bypassAttempted
                || !await DpiBypass.EnsureStartedAsync(DpiBypassEnabled).ConfigureAwait(false))
                break;
            bypassAttempted = true;
            Logger.Info("SoundCloud packet bypass started — retrying transports through it");
        }

        throw new HttpRequestException("all SoundCloud transports failed (direct, dpi-bypass, proxy, packet bypass)");
    }

    // ===================== Transport selection =====================

    /// <summary>Transport attempt chain in priority order (see the class doc).</summary>
    private static List<(HttpClient Client, string Name)> ResolveTransports()
    {
        var (mode, endpoint) = Resolve();
        var direct = (GetDirectClient(), DirectKey);

        switch (mode)
        {
            case ProxyMode.Off:
                return new List<(HttpClient, string)> { direct };

            case ProxyMode.Explicit:
                // Explicit user choice: do not try direct, even if it "works".
                return endpoint != null
                    ? new List<(HttpClient, string)> { (GetProxyClient(endpoint.Value), SoundCloudProxyConfig.Format(endpoint.Value)) }
                    : new List<(HttpClient, string)> { direct };

            default: // Auto
            {
                // Order: cached working transport first, the rest as fallbacks.
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

                // Transports that failed within the last 90 seconds go to the end of the
                // chain (the first attempt is always kept: the chain must not be empty).
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

            // Unparsable setting is already logged in ApplyUserSetting — behave as auto.
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

    // ==================== Working-mode cache ===================

    /// <summary>Working transport from the cache, or null if empty/expired.</summary>
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

            if (unchanged) return; // TTL extended, mode unchanged — don't log again
        }

        Logger.Info($"SoundCloud network mode selected: {key}");
    }

    /// <summary>Resets the selection cache. Called under <see cref="Lock"/>.</summary>
    private static void InvalidateWorkingMode()
    {
        _workingModeKey = null;
        _workingModeUntilUtc = DateTime.MinValue;
    }

    // ======================== Clients ===========================

    private static HttpClient GetDirectClient()
    {
        lock (Lock) return _directClient ??= CreateClient(proxy: null);
    }

    /// <summary>Client through the local DPI-bypass proxy (ClientHello fragmentation).</summary>
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
                return _directClient ??= CreateClient(proxy: null); // unsupported scheme — degrade to direct

            // Do not dispose the old proxy client: in-flight requests may still use it.
            // Recreation only happens on a proxy change, so sockets don't leak.
            _proxyClient = CreateClient(webProxy);
            _proxyClientKey = key;
        }

        Logger.Info($"SoundCloud HTTP client created for proxy {key}"); // credentials are not part of the endpoint
        return _proxyClient;
    }

    private static HttpClient CreateClient(WebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, // SoundCloud cookies are sent via the Cookie header
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = proxy // null — direct; socks5:// and http:// supported by SocketsHttpHandler
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
        var request = createRequest(); // NOT using: disposed by the owner together with the response
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            // Body under a read timeout: a DPI/CDN mid-download stream drop becomes a
            // regular network error instead of an infinite hang.
            return ((int)response.StatusCode, new TimeoutReadStream(stream, StreamReadTimeout),
                    new StreamOwner(response, request));
        }
        catch
        {
            // Headers/stream not received — clean up; the caller has nothing to own.
            request.Dispose();
            throw;
        }
    }

    /// <summary>Innermost exception text: the real cause lives there (DNS/timeout/refused/reset) —
    /// without it the log only shows the wrapper type.</summary>
    private static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException != null) cur = cur.InnerException;
        return cur.Message;
    }

    /// <summary>
    /// Media body wrapper: if data stops arriving for N seconds (DPI tearing the stream
    /// mid-download, CDN drop) — an IOException instead of an infinite hang. Callers catch
    /// it as a regular network failure (retry/fallback).
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

    /// <summary>Keeps response and request alive while the caller reads the body Stream.</summary>
    private sealed class StreamOwner(HttpResponseMessage response, HttpRequestMessage request) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose(); // also closes the content stream
            request.Dispose();
        }
    }

    /// <summary>
    /// Whether the error is a network one (a retry makes sense): unavailability/drop/timeout
    /// of the HTTP request, but NOT caller cancellation.
    /// </summary>
    private static bool IsRetryableNetworkError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is HttpRequestException
               || (ex is TaskCanceledException && ex.InnerException is TimeoutException); // HttpClient.Timeout fired
    }
}
