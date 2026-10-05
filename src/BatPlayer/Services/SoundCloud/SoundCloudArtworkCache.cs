using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Дисковый кэш обложек SoundCloud: качает artwork_url в локальный файл через общий
/// сетевой слой <see cref="SoundCloudHttp"/> (тот же direct ↔ прокси-фолбэк, что и у
/// API-запросов и стримов) и отдаёт путь — карточки биндят локальный файл.
///
/// Зачем: i1.sndcdn.com блокирует прямые подключения, а BitmapImage в карточках качает
/// без SOCKS-прокси пользователя — обложка не грузится. Локальный файл снимает проблему
/// и заодно делает обложки доступными офлайн.
///
/// Имя файла — {scId}.jpg; каталог по умолчанию %LOCALAPPDATA%/BatPlayer/artworks_cache/.
/// Скачивание идёт в .part и атомарно переезжает в финальное имя (обрезанный файл не попадёт
/// в кэш как валидный). Ошибки одного файла (HTTP != 200, IOException) — лог + null:
/// батч-закачка продолжается, карточка показывает плейсхолдер.
/// </summary>
public sealed class SoundCloudArtworkCache
{
    private const string Extension = ".jpg";

    private readonly string _cacheDir;

    /// <param name="cacheDir">Каталог кэша; null — %LOCALAPPDATA%/BatPlayer/artworks_cache (тесты передают временный).</param>
    public SoundCloudArtworkCache(string? cacheDir = null)
        => _cacheDir = cacheDir ?? Path.Combine(App.AppDataDir, "artworks_cache");

    /// <summary>Каталог кэша (для диагностики/тестов).</summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Путь кэш-файла обложки. scId — id трека из API (цифры); на всякий случай
    /// всё, кроме [A-Za-z0-9_-], заменяется на '_' — чтобы id из БД не вывел путь наружу каталога.
    /// </summary>
    public string GetCacheFilePath(string scId)
    {
        var safe = new string((scId ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
            .ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(_cacheDir, safe + Extension);
    }

    /// <summary>Файл уже скачан и непустой (нулевой файл — след оборванной записи).</summary>
    public bool IsCachedFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    /// <summary>
    /// Обложка трека в кэше: уже скачанная — сразу путь (без сети); иначе скачивание.
    /// Пустой artworkUrl или ошибка скачивания — null (логируется, вызывающий батч продолжает).
    /// </summary>
    public async Task<string?> EnsureDownloadedAsync(string scId, string? artworkUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(artworkUrl)) return null;

        var finalPath = GetCacheFilePath(scId);
        if (IsCachedFile(finalPath)) return finalPath;

        Directory.CreateDirectory(_cacheDir);
        var tempPath = finalPath + ".part";
        try
        {
            var (status, stream, owner) = await SoundCloudHttp.SendStreamAsync(
                () => new HttpRequestMessage(HttpMethod.Get, artworkUrl), ct);

            using (owner)
            {
                if (status != 200 || stream == null)
                {
                    Logger.Warn($"SoundCloud artwork download failed with HTTP {status} (scId {scId})");
                    return null;
                }

                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await stream.CopyToAsync(file, ct);
            }

            // Атомарная публикация: до Move в кэше лежит только .part.
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            // IOException и прочие сетевые сбои — не критично: карточка покажет плейсхолдер.
            Logger.Error(ex, $"SoundCloud artwork download failed (scId {scId})");
            TryDelete(tempPath);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SoundCloud artwork temp cleanup failed ({Path.GetFileName(path)})");
        }
    }
}
