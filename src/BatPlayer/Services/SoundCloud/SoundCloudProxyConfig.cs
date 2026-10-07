using System;
using System.Globalization;
using System.Net;
using Microsoft.Win32;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Parsing and construction of proxies for SoundCloud requests: pure string parsers
/// (unit-testable), WinINET system-proxy reading and WebProxy construction.
/// Schemes: "socks5" (SOCKS5, .NET 6+) and "http" (for https traffic the proxy works via CONNECT).
/// Proxy credentials are not supported and never logged.
/// </summary>
internal static class SoundCloudProxyConfig
{
    /// <summary>
    /// Parses the WinINET ProxyServer string (HKCU\...\Internet Settings):
    /// "socks=host:port", "host:port" (bare — HTTP proxy), "http=host:port;https=host:port",
    /// lists with ftp/gopher/&lt;local&gt;. Priority: socks5 → https → http.
    /// Empty or unparsable → null.
    /// </summary>
    internal static (string Scheme, string Host, int Port)? ParseSystemProxy(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return null;

        (string Scheme, string Host, int Port)? best = null;
        var bestRank = 0;

        foreach (var rawSegment in proxyServer.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment;
            if (string.Equals(segment, "<local>", StringComparison.OrdinalIgnoreCase)) continue;

            // "[proto=]host:port"; without a prefix WinINET treats the string as an HTTP proxy.
            var scheme = "http";
            var rank = 1;
            var eq = segment.IndexOf('=');
            if (eq >= 0)
            {
                var proto = segment[..eq].Trim().ToLowerInvariant();
                segment = segment[(eq + 1)..].Trim();

                if (proto is "socks" or "socks5") { scheme = "socks5"; rank = 3; }
                // https= — a proxy FOR https traffic (our case); it stays an HTTP CONNECT proxy,
                // preferred over plain http= but below socks5.
                else if (proto == "https") { scheme = "http"; rank = 2; }
                else if (proto == "http") { scheme = "http"; rank = 1; }
                else continue; // ftp/gopher/unknown protocols are no use to us
            }

            if (!TryParseHostPort(segment, out var host, out var port)) continue;

            // On equal rank the first parsed segment wins.
            if (best == null || rank > bestRank)
            {
                best = (scheme, host, port);
                bestRank = rank;
                if (rank == 3) break; // nothing ranks above socks5
            }
        }

        return best;
    }

    /// <summary>
    /// Parses the user SoundCloudProxy setting: "socks5://host:port", "http://host:port",
    /// "socks://host:port", no scheme — "host:port" as http. "off"/"" are handled by the
    /// caller (they are not endpoints). Unparsable string → null.
    /// </summary>
    internal static (string Scheme, string Host, int Port)? ParseUserProxy(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;

        var value = setting.Trim();
        var scheme = "http";
        var sep = value.IndexOf("://", StringComparison.Ordinal);
        if (sep >= 0)
        {
            var proto = value[..sep].Trim().ToLowerInvariant();
            scheme = proto switch
            {
                "socks" or "socks5" => "socks5",
                "http" or "https" => proto,
                _ => string.Empty
            };
            if (scheme.Length == 0) return null;
            value = value[(sep + 3)..].TrimEnd('/');
        }

        // user:pass@host:port is not supported: local proxies without auth only.
        if (value.Contains('@')) return null;

        return TryParseHostPort(value, out var host, out var port) ? (scheme, host, port) : null;
    }

    /// <summary>String form of an endpoint for logs and comparison: "socks5://127.0.0.1:10808".</summary>
    internal static string Format((string Scheme, string Host, int Port) endpoint)
        => $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}";

    /// <summary>
    /// WinINET system proxy: ProxyEnable + ProxyServer (formats — see <see cref="ParseSystemProxy"/>).
    /// A PAC script (AutoConfigURL) cannot be evaluated synchronously — returns null if only PAC is set.
    /// </summary>
    internal static (string Scheme, string Host, int Port)? ReadSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return null;

            if ((key.GetValue("ProxyEnable") as int? ?? 0) == 0) return null;

            var parsed = ParseSystemProxy(key.GetValue("ProxyServer") as string);
            if (parsed != null) return parsed;

            if (!string.IsNullOrWhiteSpace(key.GetValue("AutoConfigURL") as string))
                Logger.Info("SoundCloud proxy: system PAC script is not supported, ignoring");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud system proxy read failed");
            return null;
        }
    }

    /// <summary>
    /// WebProxy for the endpoint; null if the platform does not support the scheme (UnsupportedProxyScheme).
    /// Only scheme/host/port reach the log — no credentials (we don't have any).
    /// </summary>
    internal static WebProxy? TryCreateWebProxy((string Scheme, string Host, int Port) endpoint)
    {
        try
        {
            return new WebProxy(new Uri(Format(endpoint)));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud proxy {Format(endpoint)} is not supported by this platform");
            return null;
        }
    }

    /// <summary>
    /// "host:port" (IPv6 in brackets allowed: "[::1]:1080"). Port is required — null without it.
    /// </summary>
    private static bool TryParseHostPort(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var rest = input.Trim();
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest[..(close + 1)];
            rest = rest[(close + 1)..];
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon >= 0 ? rest[..colon] : rest;
            rest = colon >= 0 ? rest[colon..] : string.Empty;
        }

        // The remainder must be ":<port>"; the host cannot be empty.
        if (host.Length == 0 || rest.Length == 0 || rest[0] != ':') return false;

        var portText = rest[1..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return false;
        return port is >= 1 and <= 65535;
    }
}
