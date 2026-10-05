using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;
using BatPlayer.Models;

namespace BatPlayer.Audio;

/// <summary>
/// Параметрический эквалайзер на BiQuad-фильтрах с ДИНАМИЧЕСКИМ набором полос.
/// Полосы добавляются/удаляются пользователем на графике (как в FabFilter Pro-Q):
/// пустой набор = прозрачная прямая линия. Каждая полоса — peaking-фильтр со своей
/// частотой (20 Гц…20 кГц) и усилением (±12 дБ), Q = 1.41.
/// </summary>
public sealed class EqualizerSampleProvider : ISampleProvider
{
    // Стандартные частоты для дефолтных пресетов (кривая при этом рисуется
    // по фактическим частотам полос, а не по этой сетке).
    public static readonly double[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public float PreGain
    {
        get => _userPreGain;
        set { _userPreGain = value; RecomputeEffectiveGain(); }
    }

    // Слайдер PreGain (линейный множитель из дБ) и итоговый множитель выхода.
    // Итог = PreGain × авто-запас (headroom): подъёмы полос на громких мастерах,
    // записанных в упор до 0 dBFS, выталкивают сэмплы за полную шкалу — выход
    // клипует и звук «хрипит». Гасим выход ровно на максимальный подъём,
    // сохраняя форму кривой; на плоском наборе множитель равен 1.
    private float _userPreGain = 1f;
    private float _effectiveGain = 1f;

    private void RecomputeEffectiveGain()
    {
        double maxBoostDb = 0;
        for (int i = 0; i < _bandGains.Count; i++)
            if (_bandTypes[i] == EqualizerBandType.Bell && _bandGains[i] > maxBoostDb)
                maxBoostDb = _bandGains[i];
        _effectiveGain = (float)(_userPreGain * Math.Pow(10, -maxBoostDb / 20.0));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
    }

    // Набор полос: параметры + матрица фильтров [полоса][каскад][канал].
    // Срез с крутизной N дБ/окт = N/12 каскадных HP/LP биквадов (+ секция первого
    // порядка для дробных 18/30 — BiQuadFilter из NAudio своих коэффициентов не даёт).
    // Структурные изменения (add/remove/apply) строят новый массив целиком и
    // подменяют ссылку — аудиопоток всегда читает согласованный набор.
    private readonly List<double> _bandFreqs = new();
    private readonly List<double> _bandGains = new();
    private readonly List<EqualizerBandType> _bandTypes = new();
    private readonly List<int> _bandSlopes = new();
    private readonly List<double> _bandQs = new();
    private IFilterSection[][][] _bandFilters = Array.Empty<IFilterSection[][]>();

    /// <summary>Заменить весь набор полос (пресет, загрузка, удаление).</summary>
    public void ApplyBands(IReadOnlyList<EqualizerBand> bands)
    {
        _bandFreqs.Clear();
        _bandGains.Clear();
        _bandTypes.Clear();
        _bandSlopes.Clear();
        _bandQs.Clear();
        foreach (var b in bands)
        {
            _bandFreqs.Add(ClampFreq(b.Frequency));
            _bandGains.Add(b.Gain);
            _bandTypes.Add(b.Type);
            _bandSlopes.Add(b.SlopeDbOct);
            _bandQs.Add(b.Q);
        }
        RebuildAll();
        RecomputeEffectiveGain();
    }

    /// <summary>Обновить одну полосу (перетаскивание узла на графике) — без пересборки набора.</summary>
    public void UpdateBand(int index, EqualizerBand band)
    {
        if (index < 0 || index >= _bandFreqs.Count) return;
        _bandFreqs[index] = ClampFreq(band.Frequency);
        _bandGains[index] = band.Gain;
        _bandTypes[index] = band.Type;
        _bandSlopes[index] = band.SlopeDbOct;
        _bandQs[index] = band.Q;
        RebuildOne(index);
        RecomputeEffectiveGain();
    }

    private double ClampFreq(double freqHz)
    {
        // Частота не выше ~45% Найквиста, иначе фильтр теряет устойчивость.
        var maxFreq = Math.Min(20000, _source.WaveFormat.SampleRate * 0.45);
        return Math.Clamp(freqHz, 20, maxFreq);
    }

    private void RebuildAll()
    {
        var filters = new IFilterSection[_bandFreqs.Count][][];
        for (int i = 0; i < filters.Length; i++)
            filters[i] = BuildBand(i);
        _bandFilters = filters;
    }

    private void RebuildOne(int index)
    {
        var filters = _bandFilters;
        if (index < 0 || index >= filters.Length) return;
        filters[index] = BuildBand(index);
    }

    /// <summary>Каскад фильтров полосы: Bell — один peaking; срез N дБ/окт — N/12
    /// каскадных HP/LP-биквадов (Butterworth, Q=0.7071) на ту же частоту. Дробная
    /// дюжина (18/30 дБ/окт) добирается секцией ПЕРВОГО порядка (6 дБ/окт) —
    /// нечётный порядок биквадами не собирается.</summary>
    private IFilterSection[][] BuildBand(int index)
    {
        var sr = _source.WaveFormat.SampleRate;
        var freq = (float)_bandFreqs[index];
        var type = _bandTypes[index];
        var isCut = type is EqualizerBandType.LowCut or EqualizerBandType.HighCut;
        var slope = _bandSlopes[index];

        // Целые двенадцатки — каскады биквадов; хвост 6 дБ/окт (18/30) — first-order.
        var stages = isCut ? Math.Max(1, slope / 12) : 1;
        var firstOrder = isCut && slope % 12 == 6;

        var result = new IFilterSection[stages + (firstOrder ? 1 : 0)][];
        for (int s = 0; s < stages; s++)
        {
            var perChannel = new IFilterSection[_channels];
            for (int c = 0; c < _channels; c++)
            {
                perChannel[c] = new BiquadSection(type switch
                {
                    EqualizerBandType.LowCut => BiQuadFilter.HighPassFilter(sr, freq, 0.7071f),
                    EqualizerBandType.HighCut => BiQuadFilter.LowPassFilter(sr, freq, 0.7071f),
                    _ => BiQuadFilter.PeakingEQ(sr, freq, (float)_bandQs[index], (float)_bandGains[index])
                });
            }
            result[s] = perChannel;
        }

        if (firstOrder)
        {
            var perChannel = new IFilterSection[_channels];
            for (int c = 0; c < _channels; c++)
                perChannel[c] = FirstOrderCut(sr, _bandFreqs[index], type == EqualizerBandType.HighCut);
            result[stages] = perChannel;
        }
        return result;
    }

    /// <summary>ФВЧ/ФНЧ первого порядка (6 дБ/окт): билинейное преобразование
    /// аналогового H(s)=s/(s+w) для ФВЧ и H(s)=w/(s+w) для ФНЧ. BiQuadFilter из
    /// NAudio свои коэффициенты не выставляет (конструктор закрыт) — потому своя
    /// секция в прямом форме DF1.</summary>
    private static IFilterSection FirstOrderCut(int sampleRate, double freqHz, bool lowPass)
    {
        var k = (float)Math.Tan(Math.PI * freqHz / sampleRate); // prewarp половины такта
        var a1 = (k - 1) / (k + 1);
        return lowPass
            ? new FirstOrderSection(k / (1 + k), k / (1 + k), a1)
            : new FirstOrderSection(1 / (1 + k), -1 / (1 + k), a1);
    }

    /// <summary>
    /// Соло-режим «слушать гармонику»: через цепочку проходит только узкая полоса
    /// вокруг заданной частоты (2 каскадных bandpass, пик 0 дБ). Работает даже при
    /// выключенном эквалайзере — это инструмент прослушивания.
    /// </summary>
    public void SetSolo(double? freqHz, double? q)
    {
        if (freqHz == null)
        {
            _soloFilters = null;
            return;
        }
        var f = ClampFreq(freqHz.Value);
        var qq = (float)Math.Clamp(q ?? 1.41, 0.3, 8);
        var sr = _source.WaveFormat.SampleRate;
        var solo = new BiQuadFilter[_channels * 2];
        for (int c = 0; c < _channels; c++)
        {
            solo[c] = BiQuadFilter.BandPassFilterConstantPeakGain(sr, (float)f, qq);
            solo[_channels + c] = BiQuadFilter.BandPassFilterConstantPeakGain(sr, (float)f, qq);
        }
        _soloFilters = solo;
    }

    private BiQuadFilter[]? _soloFilters;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        // Соло: только выбранная гармоника, в обход выключателя эквалайзера.
        var solo = _soloFilters;
        if (solo != null)
        {
            for (int n = 0; n < read; n++)
            {
                var ch = n % _channels;
                var sample = buffer[offset + n];
                sample = solo[ch].Transform(sample);
                sample = solo[_channels + ch].Transform(sample);
                buffer[offset + n] = sample * _userPreGain;
            }
            return read;
        }

        if (!_enabled) return read;

        var filters = _bandFilters;
        if (filters.Length == 0) return read;

        for (int n = 0; n < read; n++)
        {
            var ch = n % _channels;
            var sample = buffer[offset + n];
            for (int b = 0; b < filters.Length; b++)
            {
                var stages = filters[b];
                for (int s = 0; s < stages.Length; s++)
                    sample = stages[s][ch].Transform(sample);
            }
            buffer[offset + n] = sample * _effectiveGain;
        }
        return read;
    }
}

/// <summary>Секция фильтра в каскаде полосы: NAudio-биквад либо секция первого порядка
/// (дробные крутизны 18/30 дБ/окт) — у NAudio.Dsp.BiQuadFilter коэффициенты закрыты.</summary>
internal interface IFilterSection
{
    float Transform(float sample);
}

/// <summary>Обёртка над NAudio-биквадом в интерфейс секции.</summary>
internal sealed class BiquadSection : IFilterSection
{
    private readonly BiQuadFilter _filter;
    public BiquadSection(BiQuadFilter filter) => _filter = filter;
    public float Transform(float sample) => _filter.Transform(sample);
}

/// <summary>ФВЧ/ФНЧ первого порядка (6 дБ/окт) в прямой форме DF1:
/// y[n] = b0·x[n] + b1·x[n−1] − a1·y[n−1]. Один порядок биквадом не выразить.</summary>
internal sealed class FirstOrderSection : IFilterSection
{
    // double-состояние: полюс секции стоит у единичного круга (a1 ~ -0.997 на
    // низких частотах), float-рекурсия DF1 на нём шумит заметно сильнее.
    private readonly double _b0, _b1, _a1;
    private double _x1, _y1;

    public FirstOrderSection(float b0, float b1, float a1)
    {
        _b0 = b0;
        _b1 = b1;
        _a1 = a1;
    }

    public float Transform(float x0)
    {
        var y0 = _b0 * x0 + _b1 * _x1 - _a1 * _y1;
        _x1 = x0;
        _y1 = y0;
        return (float)y0;
    }
}
