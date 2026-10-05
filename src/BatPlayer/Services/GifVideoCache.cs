using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// Кэш GIF-фонов, перекодированных в MP4. Пользовательские GIF — 700+ кадров
/// 1080p-1440p: декодер WPF держит все распакованные кадры в памяти (гигабайты)
/// и не тянет их без фризов, поэтому фон проигрывается через MediaElement
/// (аппаратное декодирование видео). Перекодирование — ffmpeg из Tools/,
/// ОДИН раз на гифку (ключ кэша — путь + размер + mtime), результат ~10-20 МБ.
/// </summary>
public static class GifVideoCache
{
    private static string CacheDir => Path.Combine(App.AppDataDir, "background_cache");

    /// <summary>Путь к bundled ffmpeg; null — нет рядом с приложением и в PATH.</summary>
    public static string? FfmpegPath
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
            if (File.Exists(local)) return local;
            var appData = Path.Combine(App.AppDataDir, "Tools", "ffmpeg.exe");
            if (File.Exists(appData)) return appData;

            var pathDirs = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathDirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* кривой элемент PATH — пропускаем */ }
            }
            return null;
        }
    }

    /// <summary>
    /// MP4 для гифки: из кэша сразу, иначе конвертация (фон-потоком). null —
    /// ffmpeg недоступен или конвертация не удалась (вызывающий код откатывается
    /// на потоковый GIF-проигрыватель).
    /// </summary>
    public static async Task<string?> GetOrCreateMp4Async(string gifPath, CancellationToken ct)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null || !File.Exists(gifPath)) return null;

        var cacheDir = CacheDir;
        Directory.CreateDirectory(cacheDir);
        var mp4 = Path.Combine(cacheDir, CacheKey(gifPath) + ".mp4");
        if (File.Exists(mp4) && new FileInfo(mp4).Length > 0) return mp4;

        // .part.mp4: ffmpeg угадывает контейнер по расширению вывода — ".tmp" не парсится.
        var tmp = mp4 + ".part.mp4";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = "-y -nostdin -hide_banner -loglevel error " +
                            $"-i \"{gifPath}\" " +
                            "-vf \"fps=12,scale='min(1280,iw)':-2:flags=lanczos\" " +
                            "-c:v libx264 -crf 23 -pix_fmt yuv420p -an -movflags +faststart " +
                            "-f mp4 " +
                            $"\"{tmp}\""
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                var stderr = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : string.Empty;
                Logger.Warn($"GIF->MP4 conversion failed (exit {process.ExitCode}): {stderr[..Math.Min(400, stderr.Length)]}");
                TryDelete(tmp);
                return null;
            }
            File.Move(tmp, mp4, overwrite: true);
            Logger.Info($"GIF converted to video: {Path.GetFileName(gifPath)} -> {new FileInfo(mp4).Length / 1048576} MB");
            return mp4;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "GIF->MP4 conversion failed");
            TryDelete(tmp);
            return null;
        }
    }

    private static string CacheKey(string gifPath)
    {
        var info = new FileInfo(gifPath);
        var seed = $"{Path.GetFullPath(gifPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..20];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* занят — не критично */ }
    }
}
