using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты парсинга прокси: строка ProxyServer из WinINET-реестра (форматы VPN-клиентов
/// "socks=127.0.0.1:10808", bare "host:port", "http=...;https=...") и пользовательская
/// настройка SoundCloudProxy ("socks5://host:port" и т.п.). Функции чистые — без сети и реестра.
/// </summary>
public class SoundCloudProxyConfigTests
{
    // ==================== ParseSystemProxy (ProxyServer) ====================

    [Theory]
    // Реальный кейс пользователя: локальный SOCKS5 от VPN.
    [InlineData("socks=127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    // Bare-строка — WinINET трактует её как HTTP-прокси.
    [InlineData("127.0.0.1:10808", "http", "127.0.0.1", 10808)]
    [InlineData("proxy.local:3128", "http", "proxy.local", 3128)]
    // Протокольный список: для https-трафика берём https= (сам прокси HTTP CONNECT).
    [InlineData("http=10.0.0.2:3128;https=10.0.0.2:3129", "http", "10.0.0.2", 3129)]
    [InlineData("https=10.0.0.2:3129", "http", "10.0.0.2", 3129)]
    [InlineData("http=10.0.0.2:3128", "http", "10.0.0.2", 3128)]
    // socks= приоритетнее http=, служебные сегменты игнорируются.
    [InlineData("ftp=1.1.1.1:21;socks=127.0.0.1:10808;<local>", "socks5", "127.0.0.1", 10808)]
    [InlineData("http=192.168.1.10:8080;socks=192.168.1.10:1080", "socks5", "192.168.1.10", 1080)]
    // Пробелы вокруг сегментов допустимы.
    [InlineData("  socks=proxy.vpn:10808  ", "socks5", "proxy.vpn", 10808)]
    // IPv6-хост в скобках.
    [InlineData("[::1]:10808", "http", "[::1]", 10808)]
    public void ParseSystemProxy_ValidFormats_ReturnsEndpoint(
        string input, string scheme, string host, int port)
    {
        var parsed = SoundCloudProxyConfig.ParseSystemProxy(input);

        Assert.NotNull(parsed);
        Assert.Equal(scheme, parsed!.Value.Scheme);
        Assert.Equal(host, parsed.Value.Host);
        Assert.Equal(port, parsed.Value.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<local>")]
    [InlineData("no-port-here")]         // порт обязателен
    [InlineData("socks=host")]           // без порта
    [InlineData("socks=host:")]          // пустой порт
    [InlineData("socks=host:0")]         // порт вне диапазона
    [InlineData("socks=host:99999")]
    [InlineData("socks=host:abc")]       // нечисловой порт
    [InlineData("gopher=host:70")]       // протокол не подходит для HTTPS-трафика
    [InlineData("socks=")]
    public void ParseSystemProxy_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(SoundCloudProxyConfig.ParseSystemProxy(input));
    }

    // ==================== ParseUserProxy (настройка) =====================

    [Theory]
    [InlineData("socks5://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("socks://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("SOCKS5://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("http://proxy.local:3128", "http", "proxy.local", 3128)]
    [InlineData("https://proxy.local:3128", "https", "proxy.local", 3128)]
    [InlineData("127.0.0.1:10808", "http", "127.0.0.1", 10808)] // без схемы → http
    [InlineData(" socks5://127.0.0.1:10808 ", "socks5", "127.0.0.1", 10808)]
    public void ParseUserProxy_ValidFormats_ReturnsEndpoint(
        string input, string scheme, string host, int port)
    {
        var parsed = SoundCloudProxyConfig.ParseUserProxy(input);

        Assert.NotNull(parsed);
        Assert.Equal(scheme, parsed!.Value.Scheme);
        Assert.Equal(host, parsed.Value.Host);
        Assert.Equal(port, parsed.Value.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]                                // "выключено" разбирает вызывающий, не парсер
    [InlineData("socks5://host")]                      // без порта
    [InlineData("socks5://host:0")]                    // порт вне диапазона
    [InlineData("ftp://host:21")]                      // схема не поддерживается
    [InlineData("socks5://user:pass@host:1080")]        // credentials не поддерживаются
    [InlineData("socks5://")]                          // пустой хост
    public void ParseUserProxy_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(SoundCloudProxyConfig.ParseUserProxy(input));
    }

    // ========================= Вспомогательные ==========================

    [Fact]
    public void Format_RendersSchemeHostPort()
    {
        var parsed = SoundCloudProxyConfig.ParseSystemProxy("socks=127.0.0.1:10808");

        Assert.Equal("socks5://127.0.0.1:10808", SoundCloudProxyConfig.Format(parsed!.Value));
    }

    [Fact]
    public void TryCreateWebProxy_Socks5AndHttp_CreatesWebProxy()
    {
        // .NET 8 поддерживает socks5:// в WebProxy/SocketsHttpHandler — создание не бросает.
        var socks = SoundCloudProxyConfig.TryCreateWebProxy(("socks5", "127.0.0.1", 10808));
        var http = SoundCloudProxyConfig.TryCreateWebProxy(("http", "127.0.0.1", 8080));

        Assert.NotNull(socks);
        Assert.Equal(new System.Uri("socks5://127.0.0.1:10808"), socks!.Address);
        Assert.NotNull(http);
        Assert.Equal(new System.Uri("http://127.0.0.1:8080"), http!.Address);
    }
}
