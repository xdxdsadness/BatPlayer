using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Proxy parsing tests: the ProxyServer string from the WinINET registry (VPN-client
/// formats like "socks=127.0.0.1:10808", bare "host:port", "http=...;https=...") and the
/// user SoundCloudProxy setting ("socks5://host:port" etc.). Pure functions — no network.
/// </summary>
public class SoundCloudProxyConfigTests
{
    // ==================== ParseSystemProxy (ProxyServer) ====================

    [Theory]
    // Real user case: local SOCKS5 from a VPN.
    [InlineData("socks=127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    // Bare string — WinINET treats it as an HTTP proxy.
    [InlineData("127.0.0.1:10808", "http", "127.0.0.1", 10808)]
    [InlineData("proxy.local:3128", "http", "proxy.local", 3128)]
    // Protocol list: https= wins for https traffic.
    [InlineData("http=10.0.0.2:3128;https=10.0.0.2:3129", "http", "10.0.0.2", 3129)]
    [InlineData("https=10.0.0.2:3129", "http", "10.0.0.2", 3129)]
    [InlineData("http=10.0.0.2:3128", "http", "10.0.0.2", 3128)]
    // socks= beats http=; service segments are ignored.
    [InlineData("ftp=1.1.1.1:21;socks=127.0.0.1:10808;<local>", "socks5", "127.0.0.1", 10808)]
    [InlineData("http=192.168.1.10:8080;socks=192.168.1.10:1080", "socks5", "192.168.1.10", 1080)]
    // Whitespace around segments is allowed.
    [InlineData("  socks=proxy.vpn:10808  ", "socks5", "proxy.vpn", 10808)]
    // Bracketed IPv6 host.
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
    [InlineData("no-port-here")]         // port required
    [InlineData("socks=host")]           // no port
    [InlineData("socks=host:")]          // empty port
    [InlineData("socks=host:0")]         // port out of range
    [InlineData("socks=host:99999")]
    [InlineData("socks=host:abc")]       // non-numeric port
    [InlineData("gopher=host:70")]       // protocol unsuitable for https traffic
    [InlineData("socks=")]
    public void ParseSystemProxy_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(SoundCloudProxyConfig.ParseSystemProxy(input));
    }

    // ==================== ParseUserProxy (user setting) =====================

    [Theory]
    [InlineData("socks5://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("socks://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("SOCKS5://127.0.0.1:10808", "socks5", "127.0.0.1", 10808)]
    [InlineData("http://proxy.local:3128", "http", "proxy.local", 3128)]
    [InlineData("https://proxy.local:3128", "https", "proxy.local", 3128)]
    [InlineData("127.0.0.1:10808", "http", "127.0.0.1", 10808)] // no scheme → http
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
    [InlineData("off")]                                // "off" is handled by the caller, not the parser
    [InlineData("socks5://host")]                      // no port
    [InlineData("socks5://host:0")]                    // port out of range
    [InlineData("ftp://host:21")]                      // unsupported scheme
    [InlineData("socks5://user:pass@host:1080")]        // credentials not supported
    [InlineData("socks5://")]                          // empty host
    public void ParseUserProxy_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(SoundCloudProxyConfig.ParseUserProxy(input));
    }

    // ========================= Helpers ==========================

    [Fact]
    public void Format_RendersSchemeHostPort()
    {
        var parsed = SoundCloudProxyConfig.ParseSystemProxy("socks=127.0.0.1:10808");

        Assert.Equal("socks5://127.0.0.1:10808", SoundCloudProxyConfig.Format(parsed!.Value));
    }

    [Fact]
    public void TryCreateWebProxy_Socks5AndHttp_CreatesWebProxy()
    {
        // .NET 8 supports socks5:// in WebProxy/SocketsHttpHandler — creation does not throw.
        var socks = SoundCloudProxyConfig.TryCreateWebProxy(("socks5", "127.0.0.1", 10808));
        var http = SoundCloudProxyConfig.TryCreateWebProxy(("http", "127.0.0.1", 8080));

        Assert.NotNull(socks);
        Assert.Equal(new System.Uri("socks5://127.0.0.1:10808"), socks!.Address);
        Assert.NotNull(http);
        Assert.Equal(new System.Uri("http://127.0.0.1:8080"), http!.Address);
    }
}
