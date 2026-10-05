using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>Простой volume-scaler в sample chain — нужен для плавной регулировки громкости.</summary>
public sealed class VolumeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public float Volume { get; set; } = 1f;

    public VolumeSampleProvider(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (int i = 0; i < read; i++)
            buffer[offset + i] *= Volume;
        return read;
    }
}
