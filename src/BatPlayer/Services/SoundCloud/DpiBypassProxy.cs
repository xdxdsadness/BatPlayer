using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Local (127.0.0.1) CONNECT proxy that fragments the TLS ClientHello to bypass
/// provider SNI/DPI blocking without a VPN (same trick as zapret/byedpi): the ClientHello
/// is sent in TCP segments so the SNI never appears whole in the packet a DPI inspects.
/// SoundCloudHttp retries failed direct requests through it (WebProxy http://127.0.0.1:port);
/// relays only see encrypted traffic. Listens on loopback only, accepts loopback clients
/// only, and tunnels only SoundCloud hosts on ports 80/443.
/// </summary>
internal static class DpiBypassProxy
{
    private static readonly object Lock = new();
    private static TcpListener? _listener;
    private static int _port;
    private static bool _started;

    /// <summary>Delay between ClientHello fragments, ms: enough for DPI to miss
    /// reassembling the packet, with no noticeable latency.</summary>
    private const int FragmentDelayMs = 25;

    /// <summary>First read size: the api-v2 ClientHello is ~300-600 bytes, with headroom
    /// for larger handshakes.</summary>
    private const int FirstChunkSize = 8192;

    /// <summary>
    /// Starts the proxy (idempotent). Returns the 127.0.0.1:port endpoint, or null if the
    /// port is taken / startup failed (caller falls back to other transports).
    /// </summary>
    public static (string Host, int Port)? EnsureStarted()
    {
        lock (Lock)
        {
            if (_started)
                return ("127.0.0.1", _port);
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _started = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SoundCloud DPI-bypass proxy start failed: {ex.Message}");
                _listener = null;
                return null;
            }
        }

        Logger.Info($"SoundCloud DPI-bypass proxy listening on 127.0.0.1:{_port}");
        _ = AcceptLoopAsync(_listener);
        return ("127.0.0.1", _port);
    }

    private static async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped (app shutdown)
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            // Accept loopback clients only (our own HttpClient).
            if (client.Client.RemoteEndPoint is IPEndPoint remote && !IPAddress.IsLoopback(remote.Address))
            {
                client.Close();
                return;
            }

            using var clientStream = client.GetStream();
            var request = await ReadConnectRequestAsync(clientStream).ConfigureAwait(false);
            if (request == null)
            {
                client.Close();
                return;
            }
            var (host, port) = request.Value;

            if (!IsAllowedTarget(host, port))
            {
                await WriteRawAsync(clientStream, "HTTP/1.1 403 Forbidden\r\n\r\n").ConfigureAwait(false);
                client.Close();
                return;
            }

            // Tunnel to the target.
            using var target = new TcpClient();
            await target.ConnectAsync(host, port).ConfigureAwait(false);
            await WriteRawAsync(clientStream, "HTTP/1.1 200 Connection established\r\n\r\n").ConfigureAwait(false);

            // The client's first payload is the TLS ClientHello: send it fragmented.
            var hello = await ReadFirstChunkAsync(clientStream).ConfigureAwait(false);
            if (hello == null || hello.Length == 0)
            {
                client.Close();
                return;
            }

            var targetStream = target.GetStream();
            await SendFragmentedAsync(targetStream, hello, host).ConfigureAwait(false);

            // Then relay encrypted traffic both ways until either side closes.
            var clientToTarget = RelayAsync(clientStream, targetStream);
            var targetToClient = RelayAsync(targetStream, clientStream);
            await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
        }
        catch
        {
            // A single tunnel failure must not kill the proxy: the client sees a drop,
            // which HttpClient treats as a request network error (transport fallback).
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    /// <summary>Reads the CONNECT request (up to \r\n\r\n) and parses host:port. null — not CONNECT.</summary>
    private static async Task<(string Host, int Port)?> ReadConnectRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[2048];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
            if (read == 0) return null;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var firstLine = text.Substring(0, text.IndexOf('\r'));
            // "CONNECT host:port HTTP/1.1"
            const string prefix = "CONNECT ";
            if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var authority = firstLine[prefix.Length..];
            var space = authority.IndexOf(' ');
            if (space > 0) authority = authority[..space];

            var colon = authority.LastIndexOf(':');
            var host = colon > 0 ? authority[..colon] : authority;
            var port = colon > 0 && int.TryParse(authority[(colon + 1)..], out var p) ? p : 443;
            return (host.Trim(), port);
        }
        return null;
    }

    /// <summary>Reads the client's first packet (the full TLS record, if TLS).</summary>
    private static async Task<byte[]?> ReadFirstChunkAsync(NetworkStream stream)
    {
        var buffer = new byte[FirstChunkSize];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        if (read <= 0) return null;

        // TLS record: length in bytes 3-4 (big-endian) after the 5-byte header.
        if (read >= 5 && buffer[0] == 0x16)
        {
            var recordLen = (buffer[3] << 8) | buffer[4];
            var need = Math.Min(5 + recordLen, buffer.Length);
            while (read < need)
            {
                var more = await stream.ReadAsync(buffer, read, need - read).ConfigureAwait(false);
                if (more <= 0) break;
                read += more;
            }
        }

        var result = new byte[read];
        Array.Copy(buffer, result, read);
        return result;
    }

    /// <summary>
    /// Sends the ClientHello in fragments: split at the SNI host-name offset (the name
    /// never appears whole in the first segment), with a pause between segments. If SNI
    /// is not found, split in half.
    /// </summary>
    private static async Task SendFragmentedAsync(NetworkStream target, byte[] hello, string host)
    {
        var split = FindSniHostOffset(hello, host) ?? hello.Length / 2;

        var head = new byte[split];
        Array.Copy(hello, head, split);
        await target.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
        await target.FlushAsync().ConfigureAwait(false);
        await Task.Delay(FragmentDelayMs).ConfigureAwait(false);

        var tail = new byte[hello.Length - split];
        Array.Copy(hello, split, tail, 0, tail.Length);
        await target.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
        await target.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Offset of the ASCII host name inside the ClientHello (SNI server_name). null — not found.</summary>
    private static int? FindSniHostOffset(byte[] hello, string host)
    {
        var needle = Encoding.ASCII.GetBytes(host);
        for (var i = 0; i <= hello.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (hello[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return null;
    }

    private static async Task RelayAsync(Stream from, Stream to)
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0) return;
                await to.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Either side dropped — normal tunnel termination.
        }
    }

    private static async Task WriteRawAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Only SoundCloud hosts on 80/443 are tunneled — the proxy is not an open relay.</summary>
    private static bool IsAllowedTarget(string host, int port)
    {
        if (port != 443 && port != 80) return false;
        return host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".soundcloud.cloud", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "soundcloud.com", StringComparison.OrdinalIgnoreCase);
    }
}
