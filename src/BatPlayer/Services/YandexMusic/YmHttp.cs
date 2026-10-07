using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// HTTP transport for Yandex Music (direct first, system proxy as fallback). YM is
/// reachable directly in the user's region, and a VPN exit often breaks authorization
/// (Yandex flags suspicious sessions) — see <see cref="TransportHttp"/>.
/// Default headers mirror the YM web client: OAuth authorization is set by the calling
/// code, and <c>X-Yandex-Music-Client: web</c> asks the API for the web JSON layout.
/// </summary>
internal static class YmHttp
{
    private static readonly TransportHttp Transport = new YmTransport();

    public static Task<(int Status, string Body)> SendWithFailoverAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => Transport.SendWithFailoverAsync(createRequest, ct);

    public static Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => Transport.SendStreamAsync(createRequest, ct);

    private sealed class YmTransport() : TransportHttp("Yandex Music")
    {
        protected override string UserAgent => YmService.UserAgent;

        protected override void ConfigureDefaultHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Yandex-Music-Client", YmService.MusicClientHeader);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Yandex-Music-Client", YmService.MusicClientHeader);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru");
        }
    }
}
