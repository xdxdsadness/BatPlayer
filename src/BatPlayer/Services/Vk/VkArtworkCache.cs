using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.Vk;

/// <summary>Cover cache for VK, backed by <see cref="ArtworkCache"/>.</summary>
public sealed class VkArtworkCache(string? cacheDir = null)
    : ArtworkCache(cacheDir, "VK")
{
    protected override Task<(int Status, System.IO.Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => VkHttp.SendStreamAsync(createRequest, ct);
}
