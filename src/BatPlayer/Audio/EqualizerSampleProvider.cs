using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;
using BatPlayer.Models;

namespace BatPlayer.Audio;

/// <summary>
/// Parametric equalizer on BiQuad filters with a DYNAMIC set of bands.
/// Bands are added/removed by the user on the graph (like FabFilter Pro-Q):
/// an empty set = a transparent straight line. Each band is a peaking filter
/// with its own frequency (20 Hz…20 kHz) and gain (±12 dB), Q = 1.41.
/// </summary>
public sealed class EqualizerSampleProvider : ISampleProvider
{
    // Standard frequencies for the default presets (the curve is drawn from the
    // actual band frequencies, not from this grid).
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

    // PreGain slider (linear multiplier converted from dB) and the final output
    // multiplier. Total = PreGain × auto headroom: band boosts on loud masters
    // recorded right up to 0 dBFS push samples past full scale — the output clips
    // and sounds raspy. Attenuate by exactly the max boost, preserving the curve
    // shape; on a flat set the multiplier is 1.
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

    // Band set: parameters + filter matrix [band][stage][channel].
    // A cut with slope N dB/oct = N/12 cascaded HP/LP biquads (+ a first-order
    // section for fractional 18/30 — NAudio's BiQuadFilter exposes no coefficients).
    // Structural changes (add/remove/apply) build a new array wholesale and swap
    // the reference — the audio thread always reads a consistent set.
    private readonly List<double> _bandFreqs = new();
    private readonly List<double> _bandGains = new();
    private readonly List<EqualizerBandType> _bandTypes = new();
    private readonly List<int> _bandSlopes = new();
    private readonly List<double> _bandQs = new();
    private IFilterSection[][][] _bandFilters = Array.Empty<IFilterSection[][]>();

    /// <summary>Replaces the whole set of bands (preset, load, deletion).</summary>
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

    /// <summary>Updates a single band (node drag on the graph) — no full rebuild.</summary>
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
        // Frequency capped at ~45% of Nyquist, otherwise the filter loses stability.
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

    /// <summary>Band filter cascade: Bell — one peaking; a cut of N dB/oct — N/12
    /// cascaded HP/LP biquads (Butterworth, Q=0.7071) at the same frequency. The
    /// fractional dozen (18/30 dB/oct) is completed with a FIRST-order section
    /// (6 dB/oct) — odd orders cannot be built from biquads.</summary>
    private IFilterSection[][] BuildBand(int index)
    {
        var sr = _source.WaveFormat.SampleRate;
        var freq = (float)_bandFreqs[index];
        var type = _bandTypes[index];
        var isCut = type is EqualizerBandType.LowCut or EqualizerBandType.HighCut;
        var slope = _bandSlopes[index];

        // Whole dozens — biquad cascades; the 6 dB/oct tail (18/30) — first-order.
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

    /// <summary>First-order HP/LP (6 dB/oct): bilinear transform of the analog
    /// H(s)=s/(s+w) for HP and H(s)=w/(s+w) for LP. NAudio's BiQuadFilter does not
    /// expose its coefficients (closed constructor) — hence a custom section in
    /// direct form DF1.</summary>
    private static IFilterSection FirstOrderCut(int sampleRate, double freqHz, bool lowPass)
    {
        var k = (float)Math.Tan(Math.PI * freqHz / sampleRate); // bilinear prewarp
        var a1 = (k - 1) / (k + 1);
        return lowPass
            ? new FirstOrderSection(k / (1 + k), k / (1 + k), a1)
            : new FirstOrderSection(1 / (1 + k), -1 / (1 + k), a1);
    }

    /// <summary>
    /// Solo mode "listen to harmonic": only a narrow band around the given frequency
    /// passes through (2 cascaded bandpasses, 0 dB peak). Works even when the
    /// equalizer is disabled — it is a listening tool.
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

        // Solo: only the selected harmonic, bypassing the equalizer's on/off switch.
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

/// <summary>A filter section in a band cascade: an NAudio biquad or a first-order section
/// (fractional slopes 18/30 dB/oct) — NAudio.Dsp.BiQuadFilter keeps its coefficients closed.</summary>
internal interface IFilterSection
{
    float Transform(float sample);
}

/// <summary>Wraps an NAudio biquad in the section interface.</summary>
internal sealed class BiquadSection : IFilterSection
{
    private readonly BiQuadFilter _filter;
    public BiquadSection(BiQuadFilter filter) => _filter = filter;
    public float Transform(float sample) => _filter.Transform(sample);
}

/// <summary>First-order HP/LP (6 dB/oct) in direct form DF1:
/// y[n] = b0·x[n] + b1·x[n−1] − a1·y[n−1]. A single order cannot be expressed as a biquad.</summary>
internal sealed class FirstOrderSection : IFilterSection
{
    // double state: the section pole sits near the unit circle (a1 ~ -0.997 at
    // low frequencies), where float DF1 recursion is noticeably noisier.
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
