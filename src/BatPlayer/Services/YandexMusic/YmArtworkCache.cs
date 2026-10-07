using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.YandexMusic;

/// <summary>Cover cache for Yandex Music, backed by <see cref="ArtworkCache"/>.</summary>
public sealed class YmArtworkCache(string? cacheDir = null)
    : ArtworkCache(cacheDir, "Yandex Music")
{
    protected override Task<(int Status, System.IO.Stream? Stream, IDisposable? Owner)> SendStreamAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
        => YmHttp.SendStreamAsync(createRequest, ct);
}
