using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>
/// HTTP transport for VK services (direct first, system proxy as fallback).
/// VK is reachable directly in the user's region, so there is no user proxy setting
/// for it — see <see cref="TransportHttp"/> for the failover mechanics.
/// </summary>
internal static class VkHttp
{
    private static readonly TransportHttp Transport = new VkTransport();

    public static Task<(int Status, string Body)> SendWithFailoverAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => Transport.SendWithFailoverAsync(createRequest, ct);

    public static Task<(int Status, Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => Transport.SendStreamAsync(createRequest, ct);

    private sealed class VkTransport() : TransportHttp("VK")
    {
        protected override string UserAgent => VkService.UserAgent;
    }
}
