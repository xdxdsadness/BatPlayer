using System;
using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// Universal N→M channel remapper for the format adaptation chain
/// (e.g. a 5.1 file on a stereo device or a 2.0 file on a 5.1 output).
/// Rules:
/// - N &lt; M: each source goes to its own output (first N channels), extra outputs are silent;
/// - N &gt; M: sources are distributed round-robin (i % M) and averaged per output —
///   front L/R keep their positions in a typical downmix (FL, FC, BL → L; FR, LFE, BR → R).
/// mono→stereo and stereo→mono are handled by built-in NAudio providers;
/// this class covers the remaining combinations.
/// </summary>
public sealed class ChannelMappingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _targetChannels;
    // For each output channel — the list of source channels averaged into it.
    private readonly int[][] _outputGroups;
    // Scratch buffer for source samples (the source is read in chunks).
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
        // Source sample buffer: up to 4096 frames per Read (~85ms at 48kHz).
        _temp = new float[sourceChannels * 4096];
    }

    private static int[][] BuildMapping(int sourceChannels, int targetChannels)
    {
        if (sourceChannels < targetChannels)
        {
            // Each source → its own output, remaining outputs silent.
            var own = new int[targetChannels][];
            for (int c = 0; c < targetChannels; c++)
                own[c] = c < sourceChannels ? new[] { c } : Array.Empty<int>();
            return own;
        }

        // Round-robin distribution: channel i goes to output i % targetChannels.
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
        // How many frames fit into the output buffer and our scratch buffer.
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
