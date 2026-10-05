using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using BatPlayer.Audio;

namespace BatPlayer.Services;

/// <summary>
/// Кэш волновой формы треков для таймлайна: пики амплитуды (0..1), посчитанные
/// по всему файлу в фоновом потоке. Кэшируется по пути файла — повторные показы
/// мгновенны. Свой прайвет-резолв аудиоридера — общий с плеером (AudioEngine).
/// </summary>
public static class WaveformCache
{
    /// <summary>Разрешение волновой формы (число столбцов данных).</summary>
    public const int BucketCount = 800;

    /// <summary>Размер окна огибающей энергии для темпа (сэмплы, ~23мс при 44.1кГц).</summary>
    private const int EnvHopSamples = 1024;

    /// <summary>Границы поиска темпа: ниже 60 почти всегда ошибка октавы,
    /// выше 200 — шестнадцатые вместо долей.</summary>
    private const double MinBpm = 60, MaxBpm = 200;

    private static readonly ConcurrentDictionary<string, float[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<float[]>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    // RMS-громкость трека (dBFS), посчитанная в том же декоде, что и пики:
    // используется нормализацией громкости (платформенные треки звучат по-разному громко).
    private static readonly ConcurrentDictionary<string, double?> LoudnessDb = new(StringComparer.OrdinalIgnoreCase);
    // Темп трека (BPM), посчитанный в том же декоде: им живёт амбиент-перелив.
    private static readonly ConcurrentDictionary<string, double?> Bpm = new(StringComparer.OrdinalIgnoreCase);

    public static Task<float[]> GetPeaksAsync(string path)
    {
        // http(s)-стримы (ЯМ до попадания в кэш) тоже валидны: CreateReader открывает
        // их через MediaFoundationReader, полная выгрузка пиков идёт в фоне пару секунд.
        var isUrl = path is not null
                    && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || (!isUrl && !File.Exists(path)))
            return Task.FromResult(Array.Empty<float>());
        if (Cache.TryGetValue(path, out var cached))
            return Task.FromResult(cached);
        return InFlight.GetOrAdd(path, p => Task.Run(() => Extract(p)));
    }

    /// <summary>RMS-громкость трека в dBFS (null — измерить не удалось / тишина).
    /// Декод общий с пиками: если извлечение уже идёт/готово, ожидание мгновенно.</summary>
    public static async Task<double?> GetLoudnessDbAsync(string path)
    {
        var isUrl = path is not null
                    && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || (!isUrl && !File.Exists(path)))
            return null;
        if (LoudnessDb.TryGetValue(path, out var done))
            return done;
        if (Cache.TryGetValue(path, out _))
            return LoudnessDb.GetValueOrDefault(path); // пики готовы, громкости нет (старый кэш сессии)
        await InFlight.GetOrAdd(path, p => Task.Run(() => Extract(p)));
        return LoudnessDb.GetValueOrDefault(path);
    }

    /// <summary>Темп трека в BPM (null — определить не удалось). Декод общий с пиками
    /// (волна и так считается для таймлайна); у уже извлечённых треков берётся мгновенно.
    /// Точность не критична: значение гоняет только скорость амбиент-перелива.</summary>
    public static async Task<double?> GetBpmAsync(string path)
    {
        var isUrl = path is not null
                    && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || (!isUrl && !File.Exists(path)))
            return null;
        if (Bpm.TryGetValue(path, out var done))
            return done;
        if (Cache.TryGetValue(path, out _))
            return Bpm.GetValueOrDefault(path); // пики готовы, BPM нет (старый кэш сессии)
        await InFlight.GetOrAdd(path, p => Task.Run(() => Extract(p)));
        return Bpm.GetValueOrDefault(path);
    }

    private static float[] Extract(string path)
    {
        try
        {
            BatPlayer.Services.Logger.Info($"[WAVE] extracting: {path}");
            using var reader = AudioEngine.CreateReader(path);
            var peaks = Array.Empty<float>();
            if (reader != null)
            {
                var sp = reader.ToSampleProvider();
                var channels = Math.Max(1, sp.WaveFormat.Channels);
                var buffer = new float[8192 * channels];

                // Максимумы по чанкам: полный декод ~2-4с в фоне для типичного трека,
                // результат кэшируется на всю сессию. Попутно — сумма квадратов сэмплов
                // для RMS-громкости (нормализация громкости платформ). Замер ГЕЙТИТСЯ:
                // чанки ниже -60 dBFS (вступления-паузы) не участвуют — иначе тихий
                // замер давал завышенное усиление и трек играл громче естественного.
                var chunkMaxima = new List<float>(2048);
                // Огибающая энергии для темпа: RMS моно-микса по окнам EnvHopSamples
                // (~23мс) — плотности достаточно, чтобы бочка/атаки дали чёткий пик.
                var energyEnv = new List<float>(4096);
                double hopSum = 0;
                var hopCount = 0;
                var envRate = sp.WaveFormat.SampleRate / (double)EnvHopSamples;
                double sumSquares = 0;
                long samples = 0;
                // Кумулятивы для гейта: тишина (чанки < -60 dBFS) не участвует в RMS.
                double gatedSquares = 0;
                long gatedSamples = 0;
                double prevSum = 0;
                long prevSamples = 0;
                int read;
                while ((read = sp.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var max = 0f;
                    for (var i = 0; i < read; i += channels)
                    {
                        var mono = 0d;
                        for (var c = 0; c < channels && c < 2; c++)
                        {
                            var v = buffer[i + c];
                            var a = Math.Abs(v);
                            if (a > max) max = a;
                            sumSquares += (double)v * v;
                            mono += v;
                            samples++;
                        }
                        mono /= Math.Min(channels, 2);
                        hopSum += mono * mono;
                        if (++hopCount >= EnvHopSamples)
                        {
                            energyEnv.Add((float)(hopSum / hopCount));
                            hopSum = 0;
                            hopCount = 0;
                        }
                    }
                    chunkMaxima.Add(max);

                    // Гейт: чанк считается «музыкальным», если его пик выше -60 dBFS.
                    if (max >= 0.001f)
                    {
                        gatedSquares += sumSquares - prevSum;
                        gatedSamples += samples - prevSamples;
                    }
                    prevSum = sumSquares;
                    prevSamples = samples;
                }

                // Приоритет — замер с гейтом; если гейт отсеял ВСЁ (чистая тишина) — общий.
                var useSquares = gatedSamples > 0 ? gatedSquares : sumSquares;
                var useSamples = gatedSamples > 0 ? gatedSamples : samples;
                LoudnessDb[path] = useSamples > 0 && useSquares > 0
                    ? Math.Max(-70.0, 10.0 * Math.Log10(useSquares / useSamples))
                    : null;

                peaks = Resample(chunkMaxima, BucketCount);
                Bpm[path] = DetectBpm(energyEnv, envRate);
            }

            BatPlayer.Services.Logger.Info($"[WAVE] extracted: {peaks.Length} buckets");
            Cache[path] = peaks;
            return peaks;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Waveform peaks extraction failed");
            Cache[path] = Array.Empty<float>();
            return Array.Empty<float>();
        }
        finally
        {
            InFlight.TryRemove(path, out _);
        }
    }

    /// <summary>Чанки → BucketCount пиков (максимум по диапазону).</summary>
    private static float[] Resample(List<float> chunkMaxima, int bucketCount)
    {
        var count = chunkMaxima.Count;
        var peaks = new float[bucketCount];
        if (count == 0) return peaks;
        for (var b = 0; b < bucketCount; b++)
        {
            var from = (int)((long)b * count / bucketCount);
            var to = (int)Math.Max(from + 1, (long)(b + 1) * count / bucketCount);
            var max = 0f;
            for (var i = from; i < to && i < count; i++)
                if (chunkMaxima[i] > max) max = chunkMaxima[i];
            peaks[b] = max;
        }
        return peaks;
    }

    // ===== Темп (BPM) по огибающей энергии =====
    // Классическая схема без FFT: онсет-поток (положительная разность энергии
    // соседних окон) → автокорреляция → гребёнка кратных лагов. Бочки/снейры дают
    // в потоке чёткие пики, автокорреляция находит период доли даже при пропущенных
    // ударах; кратные лаги (x2..x4) страхуют рисунок ритма «раз-два-три-четыре».

    /// <summary>Темп по огибающей (null — короче 10 секунд / тишина / равномерный шум).</summary>
    private static double? DetectBpm(List<float> env, double envRate)
    {
        if (envRate <= 0 || env.Count < envRate * 10) return null;

        var n = env.Count;
        var flux = new double[n];
        var fluxSum = 0d;
        for (var i = 1; i < n; i++)
        {
            var d = env[i] - env[i - 1];
            if (d > 0) { flux[i] = d; fluxSum += d; }
        }
        if (fluxSum <= 1e-7) return null;

        // Нормировка на средний онсет: автокорреляция не зависит от громкости записи.
        var mean = fluxSum / n;
        for (var i = 0; i < n; i++) flux[i] /= mean;

        // ACF до 4 секунд (лаг до 4 тактов 60 BPM): гребёнке нужны кратные лаги.
        var maxLag = Math.Min((int)(envRate * 4), n - 2);
        if (maxLag < 4) return null;
        var acf = new double[maxLag + 1];
        for (var lag = 2; lag <= maxLag; lag++)
        {
            double s = 0;
            for (var i = 0; i + lag < n; i++) s += flux[i] * flux[i + lag];
            acf[lag] = s / (n - lag);
        }

        var bestScore = 0d;
        var bestBpm = 0d;
        for (var bpm = MinBpm; bpm <= MaxBpm; bpm += 0.25)
        {
            var lag = envRate * 60.0 / bpm;
            var score = (At(acf, lag)
                         + 0.5 * At(acf, 2 * lag)
                         + At(acf, 3 * lag) / 3.0
                         + 0.25 * At(acf, 4 * lag))
                        * TempoPrior(bpm);
            if (score > bestScore) { bestScore = score; bestBpm = bpm; }
        }
        if (bestBpm <= 0 || bestScore <= 0) return null;
        return Math.Round(bestBpm, 1);
    }

    /// <summary>Линейно интерполированное значение ACF на дробном лаге.</summary>
    private static double At(double[] acf, double lag)
    {
        if (double.IsNaN(lag) || lag < 2 || lag + 1 >= acf.Length) return 0;
        var i = (int)lag;
        var frac = lag - i;
        return acf[i] * (1 - frac) + acf[i + 1] * frac;
    }

    /// <summary>Приоритет темпа: у популярной музыки доли чаще в районе 115-120 BPM.
    /// Мягкий (пол 0.65): разводит октавы 75/150, но не душит быстрые жанры
    /// (drum'n'bass 170+ детектируется своим темпом, а не половинным).</summary>
    private static double TempoPrior(double bpm)
        => 0.65 + 0.35 * Math.Exp(-Math.Pow(bpm - 118, 2) / (2 * 45.0 * 45.0));
}
