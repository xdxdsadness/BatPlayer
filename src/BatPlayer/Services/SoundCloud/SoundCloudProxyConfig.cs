using System;
using System.Globalization;
using System.Net;
using Microsoft.Win32;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Разбор и создание прокси для SoundCloud-запросов: чистые парсеры строк (юнит-тестируемые),
/// чтение системного прокси WinINET и построение WebProxy.
/// Схемы: "socks5" (SOCKS5, .NET 6+) и "http" (для https-трафика прокси работает через CONNECT).
/// Учётные данные прокси не поддерживаются и никогда не логируются.
/// </summary>
internal static class SoundCloudProxyConfig
{
    /// <summary>
    /// Разбор строки ProxyServer из WinINET (HKCU\...\Internet Settings):
    /// "socks=host:port", "host:port" (bare — HTTP-прокси), "http=host:port;https=host:port",
    /// списки с ftp/gopher/&lt;local&gt;. Приоритет: socks5 → https → http.
    /// Пустая или неразбираемая → null.
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

            // "[proto=]host:port"; без префикса WinINET трактует строку как HTTP-прокси.
            var scheme = "http";
            var rank = 1;
            var eq = segment.IndexOf('=');
            if (eq >= 0)
            {
                var proto = segment[..eq].Trim().ToLowerInvariant();
                segment = segment[(eq + 1)..].Trim();

                if (proto is "socks" or "socks5") { scheme = "socks5"; rank = 3; }
                // https= — прокси ДЛЯ https-трафика (наш случай), сам он остаётся HTTP CONNECT-прокси;
                // предпочтительнее общего http=, но ниже socks5.
                else if (proto == "https") { scheme = "http"; rank = 2; }
                else if (proto == "http") { scheme = "http"; rank = 1; }
                else continue; // ftp/gopher/неизвестные протоколы нам не подходят
            }

            if (!TryParseHostPort(segment, out var host, out var port)) continue;

            // При равном ранге остаётся первый разобранный сегмент.
            if (best == null || rank > bestRank)
            {
                best = (scheme, host, port);
                bestRank = rank;
                if (rank == 3) break; // выше socks5 уже не бывает
            }
        }

        return best;
    }

    /// <summary>
    /// Разбор пользовательской настройки SoundCloudProxy: "socks5://host:port", "http://host:port",
    /// "socks://host:port", без схемы — "host:port" как http. Значения "off"/"" разбирает вызывающий
    /// (они не являются эндпоинтом). Неразбираемая строка → null.
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

        // user:pass@host:port не поддерживаем: локальный прокси без авторизации.
        if (value.Contains('@')) return null;

        return TryParseHostPort(value, out var host, out var port) ? (scheme, host, port) : null;
    }

    /// <summary>Строковое представление эндпоинта для логов и сравнения: "socks5://127.0.0.1:10808".</summary>
    internal static string Format((string Scheme, string Host, int Port) endpoint)
        => $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}";

    /// <summary>
    /// Системный прокси WinINET: ProxyEnable + ProxyServer (форматы — см. <see cref="ParseSystemProxy"/>).
    /// PAC-скрипт (AutoConfigURL) синхронно не вычисляется — при наличии только PAC возвращаем null.
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
    /// WebProxy для эндпоинта; null — если платформа не поддерживает схему (UnsupportedProxyScheme).
    /// В лог попадает только схема/хост/порт — без credentials (их у нас и нет).
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
    /// "host:port" (возможен IPv6 в скобках: "[::1]:1080"). Порт обязателен — без него null.
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

        // Остаток должен быть ":<port>"; хост пустым быть не может.
        if (host.Length == 0 || rest.Length == 0 || rest[0] != ':') return false;

        var portText = rest[1..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return false;
        return port is >= 1 and <= 65535;
    }
}
