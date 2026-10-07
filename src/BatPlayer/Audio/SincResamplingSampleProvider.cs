using NAudio.Dsp;
using NAudio.Wave;

namespace BatPlayer.Audio;

/// <summary>
/// High-quality resampler on WDL Resampler in sinc mode. The stock NAudio
/// WdlResamplingSampleProvider creates the resampler in linear interpolation mode
/// (SetMode(interp: true, filtercnt: 2, sinc: false)): linear interpolation plus a
/// cascade of IIR filters noticeably muffles high frequencies — at 44.1→48 kHz the
/// attenuation grows from ~-4 dB at 16 kHz to ~-6 dB by 20 kHz, sounding "duller"
/// than browsers (which resample with a sinc filter). Here windowed sinc is enabled
/// (SetMode: sinc=true overrides interp/filtercnt, see WDL SetMode) — frequency-
/// transparent resampling; 64 points / table of 32 is accurate enough and cheap on
/// CPU for realtime playback.
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
        // sinc=true overrides interp and filtercnt — max-quality mode.
        _resampler.SetMode(interp: true, filtercnt: 0, sinc: true, sinc_size: 64, sinc_interpsize: 32);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        // Without SetRates the resampler's rate ratio stays 1:1: samples pass
        // through untouched, and a 44.1 kHz file on a 48 kHz device plays ~8.8%
        // faster and higher in pitch (sine test: 1000 Hz -> 1088 Hz).
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
