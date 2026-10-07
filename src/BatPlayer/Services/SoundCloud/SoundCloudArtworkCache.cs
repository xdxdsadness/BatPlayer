using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>Cover cache for SoundCloud, backed by <see cref="ArtworkCache"/>.</summary>
public sealed class SoundCloudArtworkCache(string? cacheDir = null)
    : ArtworkCache(cacheDir, "SoundCloud")
{
    protected override Task<(int Status, System.IO.Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => SoundCloudHttp.SendStreamAsync(createRequest, ct);
}
