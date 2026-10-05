using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Локальный (127.0.0.1) CONNECT-прокси с фрагментацией TLS ClientHello — обход
/// SNI-блокировок провайдера БЕЗ VPN и внешних серверов. Принцип тот же, что у
/// открытых утилит (zapret/byedpi): ClientHello отправляется в TCP-сегментах так,
/// чтобы имя сервера (SNI) оказалось в отдельном сегменте — DPI, собирающий блок-
/// решение по первому пакету, не видит запрещённое имя целиком.
///
/// Как используется: SoundCloudHttp в auto-режиме пробует direct, при сетевой ошибке
/// повторяет запрос через этот прокси (HttpClient с WebProxy http://127.0.0.1:port —
/// для https он шлёт CONNECT, дальше туннель). Релеи видят только шифрованный трафик.
///
/// Безопасность: слушает ТОЛЬКО loopback, принимает соединения только с loopback,
/// туннелирует только хосты SoundCloud (*.soundcloud.com / *.sndcdn.com /
/// *.soundcloud.cloud) на порты 80/443.
/// </summary>
internal static class DpiBypassProxy
{
    private static readonly object Lock = new();
    private static TcpListener? _listener;
    private static int _port;
    private static bool _started;

    /// <summary>Пауза между фрагментами ClientHello, мс. Достаточно, чтобы DPI
    /// «закрыл окно» сборки пакета; заметной задержки не даёт.</summary>
    private const int FragmentDelayMs = 25;

    /// <summary>Размер первого чтения: TLS ClientHello для api-v2 ~300-600 байт,
    /// запас на случай больших ClientHello (л bridges).</summary>
    private const int FirstChunkSize = 8192;

    /// <summary>
    /// Запустить прокси (идемпотентно). Возвращает эндпоинт 127.0.0.1:port или
    /// null — порт занят/старт не удался (фолбэк на другие транспорты).
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
                return; // listener остановлен (завершение приложения)
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            // Принимаем только loopback-клиентов (свой же HttpClient).
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

            // Туннель к цели.
            using var target = new TcpClient();
            await target.ConnectAsync(host, port).ConfigureAwait(false);
            await WriteRawAsync(clientStream, "HTTP/1.1 200 Connection established\r\n\r\n").ConfigureAwait(false);

            // Первый payload клиента = TLS ClientHello: отправляем фрагментами.
            var hello = await ReadFirstChunkAsync(clientStream).ConfigureAwait(false);
            if (hello == null || hello.Length == 0)
            {
                client.Close();
                return;
            }

            var targetStream = target.GetStream();
            await SendFragmentedAsync(targetStream, hello, host).ConfigureAwait(false);

            // Дальше — двусторонняя перекачка шифрованного трафика до закрытия любой из сторон.
            var clientToTarget = RelayAsync(clientStream, targetStream);
            var targetToClient = RelayAsync(targetStream, clientStream);
            await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
        }
        catch
        {
            // Ошибка одного туннеля не должна ронять прокси: клиент получит обрыв,
            // HttpClient отработает это как сетевую ошибку запроса (фолбэк транспортов).
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    /// <summary>Читает строку CONNECT (до \r\n\r\n) и парсит host:port. null — не CONNECT.</summary>
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

    /// <summary>Читает первый пакет клиента (TLS-запись целиком, если это TLS).</summary>
    private static async Task<byte[]?> ReadFirstChunkAsync(NetworkStream stream)
    {
        var buffer = new byte[FirstChunkSize];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        if (read <= 0) return null;

        // TLS record: длина в байтах 3-4 (big-endian) после заголовка 5 байт.
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
    /// Отправить ClientHello фрагментами: разрез ПО смещению имени хоста внутри SNI
    /// (в первом сегменте имя не встречается целиком), между сегментами пауза.
    /// Если SNI не нашёлся — разрез пополам.
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

    /// <summary>Смещение ASCII-имени хоста внутри ClientHello (SNI server_name). null — не нашлось.</summary>
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
            // Обрыв одной из сторон — нормальное завершение туннеля.
        }
    }

    private static async Task WriteRawAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Туннелируем только хосты SoundCloud на 80/443 — прокси не «открытый релей».</summary>
    private static bool IsAllowedTarget(string host, int port)
    {
        if (port != 443 && port != 80) return false;
        return host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".soundcloud.cloud", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "soundcloud.com", StringComparison.OrdinalIgnoreCase);
    }
}
