using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Дисковый кэш mp3-стримов VK: обёртка над <see cref="Services.SoundCloud.SoundCloudStreamCache"/>
/// с собственным каталогом (%LOCALAPPDATA%/BatPlayer/vk_cache). Внутренняя механика
/// (скачивание в .part + атомарная публикация, сериализация параллельных запросов одного
/// трека, лимит каталога с вытеснением старых файлов) полностью переиспользуется; меняется
/// только каталог кэша — vk_id ("{owner_id}_{id}", только цифры и '_') напрямую пригоден
/// как имя файла. Отдельный тип нужен, чтобы в DI жить рядом с SC-кэшем, а не заменять его.
///
/// Скачивание выполняет общий сетевой слой SoundCloudHttp (тот же direct → системный
/// прокси-фолбэк, что у VK-запросов; UA — общий браузерный, VK CDN его не различает).
///
/// Скачивание ФАЙЛОВ пользователю не предоставляется: кэш существует только для
/// стриминга (повторный клик играет офлайн без сети).
/// </summary>
public sealed class VkStreamCache
{
    private readonly SoundCloud.SoundCloudStreamCache _inner;

    /// <param name="cacheDir">Каталог кэша; null — %LOCALAPPDATA%/BatPlayer/vk_cache (тесты передают временный).</param>
    public VkStreamCache(string? cacheDir = null)
        => _inner = new SoundCloud.SoundCloudStreamCache(cacheDir ?? Path.Combine(App.AppDataDir, "vk_cache"));

    /// <summary>Каталог кэша (для диагностики/тестов).</summary>
    public string CacheDir => _inner.CacheDir;

    /// <summary>mp3 трека уже в кэше (файл существует и непустой) — можно играть офлайн без сети.</summary>
    public bool IsTrackCached(string vkId) => _inner.IsTrackCached(vkId);

    /// <summary>Путь кэш-файла для трека: vk_cache/{vk_id}.mp3 (vk_id — "{owner_id}_{id}").</summary>
    public string GetCacheFilePath(string vkId) => _inner.GetCacheFilePath(vkId);

    /// <summary>
    /// Файл трека в кэше: уже скачанный — сразу путь; иначе скачивание стрима по
    /// временной ссылке и путь к нему. Бросает исключение на HTTP-неуспехе (файл .part удаляется).
    /// </summary>
    public Task<string> GetStreamFileAsync(string streamUrl, string vkId, CancellationToken ct)
        => _inner.GetStreamFileAsync(streamUrl, vkId, ct);
}
