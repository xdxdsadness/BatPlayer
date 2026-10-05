using System;
using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// Универсальный ремаппер каналов N→M для цепочки адаптации формата
/// (например 5.1-файл на stereo-устройстве или 2.0-файл на 5.1-выходе).
/// Правила:
/// - N &lt; M: каждый источник идёт в свой выход (первые N каналов), лишние выходы — тишина;
/// - N &gt; M: источники раскладываются по кругу (i % M) и усредняются внутри каждого выхода —
///   фронт L/R сохраняет позиции при типичном downmix (FL, FC, BL → L; FR, LFE, BR → R).
/// mono→stereo и stereo→mono обрабатываются штатными NAudio-провайдерами,
/// этот класс нужен для остальных комбинаций.
/// </summary>
public sealed class ChannelMappingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _targetChannels;
    // Для каждого выходного канала — список исходных каналов, которые усредняются в него.
    private readonly int[][] _outputGroups;
    // Промежуточный буфер исходных сэмплов (чтение из источника порциями).
    private readonly float[] _temp;

    public WaveFormat WaveFormat { get; }

    public ChannelMappingSampleProvider(ISampleProvider source, int targetChannels)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (targetChannels < 1)
            throw new ArgumentOutOfRangeException(nameof(targetChannels), "Target channel count must be positive");

        var sourceChannels = source.WaveFormat.Channels;
        _targetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);

        _outputGroups = BuildMapping(sourceChannels, targetChannels);
        // Буфер исходных сэмплов: до 4096 кадров за один Read (~85мс при 48кГц).
        _temp = new float[sourceChannels * 4096];
    }

    private static int[][] BuildMapping(int sourceChannels, int targetChannels)
    {
        if (sourceChannels < targetChannels)
        {
            // Каждый источник → свой выход, оставшиеся выходы немые.
            var own = new int[targetChannels][];
            for (int c = 0; c < targetChannels; c++)
                own[c] = c < sourceChannels ? new[] { c } : Array.Empty<int>();
            return own;
        }

        // Раскладка по кругу: канал i попадает в выход i % targetChannels.
        var counts = new int[targetChannels];
        for (int i = 0; i < sourceChannels; i++)
            counts[i % targetChannels]++;

        var groups = new int[targetChannels][];
        var fills = new int[targetChannels];
        for (int c = 0; c < targetChannels; c++)
            groups[c] = new int[counts[c]];
        for (int i = 0; i < sourceChannels; i++)
        {
            var c = i % targetChannels;
            groups[c][fills[c]++] = i;
        }
        return groups;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var sourceChannels = _source.WaveFormat.Channels;
        // Сколько кадров помещается в выходной буфер и в наш scratch-буфер.
        var frames = Math.Min(count / _targetChannels, _temp.Length / sourceChannels);
        if (frames <= 0) return 0;

        var read = _source.Read(_temp, 0, frames * sourceChannels);
        var outFrames = read / sourceChannels;

        for (int f = 0; f < outFrames; f++)
        {
            var srcBase = f * sourceChannels;
            var dstBase = offset + f * _targetChannels;
            for (int outCh = 0; outCh < _targetChannels; outCh++)
            {
                var group = _outputGroups[outCh];
                if (group.Length == 0)
                {
                    buffer[dstBase + outCh] = 0f;
                }
                else if (group.Length == 1)
                {
                    buffer[dstBase + outCh] = _temp[srcBase + group[0]];
                }
                else
                {
                    var sum = 0f;
                    for (int g = 0; g < group.Length; g++)
                        sum += _temp[srcBase + group[g]];
                    buffer[dstBase + outCh] = sum / group.Length;
                }
            }
        }

        return outFrames * _targetChannels;
    }
}
