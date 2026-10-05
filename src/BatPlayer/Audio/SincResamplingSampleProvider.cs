using NAudio.Dsp;
using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// Ресемплер высокого качества на WDL Resampler в sinc-режиме.
/// Штатный NAudio WdlResamplingSampleProvider создаёт ресемплер в режиме линейной
/// интерполяции (SetMode(interp: true, filtercnt: 2, sinc: false)): линейная
/// интерполяция плюс каскад IIR-фильтров заметно глушат верхние частоты —
/// при 44.1→48 кГц ослабление растёт от ~-4 дБ на 16 кГц до ~-6 дБ к 20 кГц,
/// звук воспринимается «глуше», чем в браузерах (те ресемплируют sinc-фильтром).
/// Здесь включён оконный sinc (SetMode: sinc=true перекрывает interp/filtercnt,
/// см. WDL SetMode) — частотно-прозрачный ресемплинг; 64 точки / таблица 32
/// достаточно точны и дешевы по CPU для realtime-воспроизведения.
/// </summary>
public sealed class SincResamplingSampleProvider : ISampleProvider
{
    private readonly WdlResampler _resampler = new();
    private readonly ISampleProvider _source;
    private readonly WaveFormat _outFormat;

    public SincResamplingSampleProvider(ISampleProvider source, int outSampleRate)
    {
        _source = source;
        _outFormat = WaveFormat.CreateIeeeFloatWaveFormat(outSampleRate, source.WaveFormat.Channels);
        // SetMode(interp, filtercnt, sinc, sinc_size, sinc_interpsize):
        // sinc=true перекрывает interp и filtercnt — режим максимального качества.
        _resampler.SetMode(interp: true, filtercnt: 0, sinc: true, sinc_size: 64, sinc_interpsize: 32);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        // Без SetRates соотношение частот в ресемплере остаётся 1:1: он пропускает
        // сэмплы насквозь, и файл 44.1 кГц на устройстве 48 кГц играет на ~8.8%
        // быстрее и выше по тону (синус-тест: 1000 Гц -> 1088 Гц).
        _resampler.SetRates(source.WaveFormat.SampleRate, outSampleRate);
    }

    public WaveFormat WaveFormat => _outFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _outFormat.Channels;
        var framesRequested = count / channels;
        int inNeeded = _resampler.ResamplePrepare(framesRequested, channels, out float[] inBuffer, out int inBufferOffset);
        int inAvailable = _source.Read(inBuffer, inBufferOffset, inNeeded * channels);
        return _resampler.ResampleOut(buffer, offset, inAvailable / channels, framesRequested, channels) * channels;
    }
}
