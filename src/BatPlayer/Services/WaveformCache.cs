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
/// Waveform cache for the timeline: amplitude peaks (0..1) computed over the whole
/// file in a background thread. Cached by file path — repeat shows are instant.
/// Has its own private audio-reader resolve, shared with the player (AudioEngine).
/// </summary>
public static class WaveformCache
{
    /// <summary>Waveform resolution (number of data columns).</summary>
    public const int BucketCount = 800;

    private static readonly ConcurrentDictionary<string, float[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<float[]>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    // RMS loudness of the track (dBFS), computed in the same decode as the peaks;
    // consumed by volume normalization (platform tracks play at different levels).
    private static readonly ConcurrentDictionary<string, double?> LoudnessDb = new(StringComparer.OrdinalIgnoreCase);

    public static Task<float[]> GetPeaksAsync(string path)
    {
        // http(s) streams (YM before entering the cache) are also valid: CreateReader
        // opens them via MediaFoundationReader; full peak extraction runs in the
        // background for a couple of seconds.
        var isUrl = path is not null
                    && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || (!isUrl && !File.Exists(path)))
            return Task.FromResult(Array.Empty<float>());
        if (Cache.TryGetValue(path, out var cached))
            return Task.FromResult(cached);
        return InFlight.GetOrAdd(path, p => Task.Run(() => Extract(p)));
    }

    /// <summary>Track RMS loudness in dBFS (null — could not measure / silence).
    /// Decoding is shared with the peaks: if extraction is already running or done,
    /// the wait is instant.</summary>
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
            return LoudnessDb.GetValueOrDefault(path); // peaks ready, loudness missing (old session cache)
        await InFlight.GetOrAdd(path, p => Task.Run(() => Extract(p)));
        return LoudnessDb.GetValueOrDefault(path);
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

                // Chunk maxima: full decode takes ~2-4s in the background, the result is
                // cached for the session. RMS loudness is gated: chunks below -60 dBFS
                // (intros/silence) are excluded, otherwise a quiet measurement inflated
                // the gain and tracks played louder than natural.
                var chunkMaxima = new List<float>(2048);
                double sumSquares = 0;
                long samples = 0;
                // Cumulative sums for the gate: silence does not count into the RMS.
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
                        for (var c = 0; c < channels && c < 2; c++)
                        {
                            var v = buffer[i + c];
                            var a = Math.Abs(v);
                            if (a > max) max = a;
                            sumSquares += (double)v * v;
                            samples++;
                        }
                    }
                    chunkMaxima.Add(max);

                    // A chunk counts as "musical" when its peak is above -60 dBFS.
                    if (max >= 0.001f)
                    {
                        gatedSquares += sumSquares - prevSum;
                        gatedSamples += samples - prevSamples;
                    }
                    prevSum = sumSquares;
                    prevSamples = samples;
                }

                // Prefer the gated measurement; when it filtered out everything, use the total.
                var useSquares = gatedSamples > 0 ? gatedSquares : sumSquares;
                var useSamples = gatedSamples > 0 ? gatedSamples : samples;
                LoudnessDb[path] = useSamples > 0 && useSquares > 0
                    ? Math.Max(-70.0, 10.0 * Math.Log10(useSquares / useSamples))
                    : null;

                peaks = Resample(chunkMaxima, BucketCount);
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

    /// <summary>Chunks → BucketCount peaks (max over the range).</summary>
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
}
