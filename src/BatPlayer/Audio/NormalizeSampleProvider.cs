using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// Volume normalization: the multiplier approaches the target value with an
/// exponential ramp. The target arrives ASYNCHRONOUSLY (the track's RMS loudness
/// is computed in the background for a couple of seconds after start) — without
/// a ramp, applying it would click mid-music. Sits in the chain after the
/// equalizer, before the user volume.
/// </summary>
public sealed class NormalizeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _current = 1f;
    private float _target = 1f;

    /// <summary>Linear target multiplier (1 — normalization off).</summary>
    public float Target
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 4f);
    }

    public NormalizeSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (_current != _target)
        {
            // Exponential approach: the distance collapses over ~a dozen blocks
            // (a 1024+ sample block ≈ 20-25ms) — the change is inaudible.
            var diff = _target - _current;
            _current = Math.Abs(diff) < 0.0005f ? _target : _current + diff * 0.25f;
        }
        if (_current == 1f) return read; // normalization off — read as-is
        for (int i = 0; i < read; i++)
            buffer[offset + i] *= _current;
        return read;
    }
}
