using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// Нормализация громкости: множитель доезжает до целевого значения экспоненциальной
/// рампой. Целевой множитель приезжает АСИНХРОННО (RMS-громкость трека считается
/// в фоне пару секунд после старта) — без рампы установка была бы щелчком посреди
/// музыки. Стоит в цепи после эквалайзера, до громкости пользователя.
/// </summary>
public sealed class NormalizeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _current = 1f;
    private float _target = 1f;

    /// <summary>Линейный целевой множитель (1 — без нормализации).</summary>
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
            // Экспоненциальное приближение: дистанция схлопывается за ~десяток блоков
            // (блок 1024+ сэмплов ≈ 20-25 мс) — установка незаметна на слух.
            var diff = _target - _current;
            _current = Math.Abs(diff) < 0.0005f ? _target : _current + diff * 0.25f;
        }
        if (_current == 1f) return read; // нормализация выключена — читаем как есть
        for (int i = 0; i < read; i++)
            buffer[offset + i] *= _current;
        return read;
    }
}
