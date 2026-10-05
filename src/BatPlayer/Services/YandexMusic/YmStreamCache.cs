using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Дисковый кэш mp3-стримов Яндекс Музыки: обёртка над
/// <see cref="Services.SoundCloud.SoundCloudStreamCache"/> с собственным каталогом
/// (%LOCALAPPDATA%/BatPlayer/ym_cache). Внутренняя механика (скачивание в .part +
/// атомарная публикация, сериализация параллельных запросов одного трека, лимит каталога
/// с вытеснением старых файлов) полностью переиспользуется; меняется только каталог кэша —
/// ym_id (цифры) напрямую пригоден как имя файла. Отдельный тип нужен, чтобы в DI жить
/// рядом с SC/VK-кэшами, а не заменять их.
///
/// Скачивание выполняет прямой слой <see cref="SoundCloudHttp"/> (тот же direct → системный
/// прокси-фолбэк, что у YM-запросов; UA — общий браузерный, CDN Яндекса его не различает).
///
/// Скачивание ФАЙЛОВ пользователю не предоставляется: кэш существует только для
/// стриминга (повторный клик играет офлайн без сети).
/// </summary>
public sealed class YmStreamCache
{
    private readonly SoundCloud.SoundCloudStreamCache _inner;

    /// <param name="cacheDir">Каталог кэша; null — %LOCALAPPDATA%/BatPlayer/ym_cache (тесты передают временный).</param>
    public YmStreamCache(string? cacheDir = null)
        => _inner = new SoundCloud.SoundCloudStreamCache(cacheDir ?? Path.Combine(App.AppDataDir, "ym_cache"));

    /// <summary>Каталог кэша (для диагностики/тестов).</summary>
    public string CacheDir => _inner.CacheDir;

    /// <summary>mp3 трека уже в кэше (файл существует и непустой) — можно играть офлайн без сети.</summary>
    public bool IsTrackCached(string ymId) => _inner.IsTrackCached(ymId);

    /// <summary>Путь кэш-файла для трека: ym_cache/{ym_id}.mp3.</summary>
    public string GetCacheFilePath(string ymId) => _inner.GetCacheFilePath(ymId);

    /// <summary>
    /// Файл трека в кэше: уже скачанный — сразу путь; иначе скачивание стрима по
    /// временной ссылке и путь к нему. Бросает исключение на HTTP-неуспехе (файл .part удаляется).
    /// </summary>
    public Task<string> GetStreamFileAsync(string streamUrl, string ymId, CancellationToken ct)
        => _inner.GetStreamFileAsync(streamUrl, ymId, ct);
}
