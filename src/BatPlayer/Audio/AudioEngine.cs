using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using BatPlayer.Localization;
using BatPlayer.Services;

namespace BatPlayer.Audio;

/// <summary>
/// Low-level NAudio audio engine. Handles:
/// - file reading (Mp3FileReader, WaveFileReader, AiffFileReader, VorbisWaveReader for OGG,
///   FlacReader for FLAC, MediaFoundationReader as fallback for everything else);
/// - output via WasapiOut (shared by default, exclusive optional);
/// - format adaptation to the device (channels + resampling to MixFormat) — a track
///   must not fail to play just because the file and device formats differ;
/// - effect chain: (adaptation) -> Equalizer -> VolumeSampleProvider -> Output.
/// Contains no playlist logic — current track only.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    // Event-sync mode allows lower latency without glitches; 120ms leaves headroom
    // against crackling at UI/CPU peaks (scrolling, cover decodes) — at 80ms the
    // dropouts are audible as noise over the track.
    private const int SharedLatencyMs = 120;
    // Exclusive is more sensitive to buffer size (device period alignment);
    // keep the proven value — failed init means losing bit-perfect mode.
    private const int ExclusiveLatencyMs = 100;

    private WaveStream? _reader;
    private ISampleProvider? _sampleProvider;
    private WasapiOut? _output;
    private EqualizerSampleProvider? _equalizer;
    private NormalizeSampleProvider? _normalize;
    private VolumeSampleProvider? _volumeProvider;
    private AudioEndpointVolume? _endpointVolume;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused  => _output?.PlaybackState == PlaybackState.Paused;

    public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalTime   => _reader?.TotalTime ?? TimeSpan.Zero;

    // Logical volume and the mute flag are stored separately from the provider's
    // volume: IsMuted used to be derived from "Volume == 0", so muting saved
    // volume as 0 and sound never returned after unmute.
    private float _volume = 1f;
    private bool _muted;
    private float _fadeTarget = 1f;
    private CancellationTokenSource? _fadeCts;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null)
                _volumeProvider.Volume = _muted ? 0f : _volume;
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_volumeProvider != null)
                _volumeProvider.Volume = _muted ? 0f : _volume;
        }
    }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public event EventHandler? PositionChanged;

    public AudioEngine()
    {
        var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _endpointVolume = defaultDevice.AudioEndpointVolume;
    }

    /// <summary>Lists available WASAPI output devices.</summary>
    public static IEnumerable<MMDevice> EnumerateDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    /// <summary>Opens a file and prepares it for playback; does not start playing automatically.</summary>
    public void Open(string filePath, bool useExclusiveMode, string? deviceFriendlyName = null)
    {
        Stop();
        DisposeReader();

        _reader = CreateReader(filePath);
        if (_reader == null)
            throw new NotSupportedException($"{Loc.Get("ErrorUnsupportedFormat")}: {filePath}");

        var device = ResolveDevice(deviceFriendlyName);

        // Exclusive: bit-perfect attempt with the file's native format (no resampling
        // or channel remap). If the device is busy or the format is unsupported,
        // fall back to shared with adaptation — a track must not fail over format.
        if (useExclusiveMode)
        {
            try
            {
                var nativeChain = BuildExclusiveChain(_reader);
                var exclusive = new WasapiOut(device, AudioClientShareMode.Exclusive, useEventSync: true, ExclusiveLatencyMs);
                try
                {
                    exclusive.Init(nativeChain);
                }
                catch
                {
                    exclusive.Dispose();
                    throw;
                }
                _output = exclusive;
                _output.PlaybackStopped += HandleOutputStopped;
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"WASAPI exclusive init failed for \"{filePath}\", falling back to shared mode");
                // The exclusive-attempt chain is no longer used.
                _equalizer = null;
                _volumeProvider = null;
            }
        }

        var sharedChain = BuildSharedChain(_reader, device);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, SharedLatencyMs);
        _output.PlaybackStopped += HandleOutputStopped;
        _output.Init(sharedChain);
    }

    /// <summary>
    /// Bit-perfect chain for exclusive mode: native file format, no adaptation.
    /// EQ runs at the source rate — no resampling, band frequencies stay correct.
    /// </summary>
    private ISampleProvider BuildExclusiveChain(WaveStream reader)
    {
        _sampleProvider = reader.ToSampleProvider();
        _equalizer = new EqualizerSampleProvider(_sampleProvider);
        _normalize = new NormalizeSampleProvider(_equalizer);
        _volumeProvider = new VolumeSampleProvider(_normalize) { Volume = _muted ? 0f : _volume };
        return _volumeProvider;
    }

    /// <summary>
    /// Shared chain adapted to the device mix format:
    /// reader -> channel adapter -> resampling (WDL) -> Equalizer -> Volume.
    /// EQ sits AFTER resampling: band frequencies must match the actual output
    /// rate, not the file rate.
    /// </summary>
    private ISampleProvider BuildSharedChain(WaveStream reader, MMDevice device)
    {
        var source = reader.ToSampleProvider();
        var sourceFormat = source.WaveFormat;

        int targetRate;
        int targetChannels;
        try
        {
            // AudioClient.MixFormat is the target session format for shared mode.
            using var audioClient = device.AudioClient;
            targetRate = audioClient.MixFormat.SampleRate;
            targetChannels = audioClient.MixFormat.Channels;
        }
        catch (Exception ex)
        {
            // MixFormat unavailable (rare drivers) — play in the file's format;
            // WASAPI shared will convert it if it can.
            Logger.Error(ex, "Failed to read device MixFormat, using source format");
            targetRate = sourceFormat.SampleRate;
            targetChannels = sourceFormat.Channels;
        }

        if (targetChannels >= 1 && targetRate > 0)
        {
            // Channel adaptation (common cases use built-in NAudio providers).
            if (sourceFormat.Channels == 1 && targetChannels == 2)
                source = new MonoToStereoSampleProvider(source);
            else if (sourceFormat.Channels == 2 && targetChannels == 1)
                source = new StereoToMonoSampleProvider(source);
            else if (sourceFormat.Channels == 1 && targetChannels > 2)
                source = new ChannelMappingSampleProvider(new MonoToStereoSampleProvider(source), targetChannels);
            else if (sourceFormat.Channels != targetChannels)
                source = new ChannelMappingSampleProvider(source, targetChannels);

            // Resample to the mix-format rate using WDL sinc mode (the stock NAudio
            // provider uses linear interpolation and muffles high frequencies —
            // see SincResamplingSampleProvider).
            if (source.WaveFormat.SampleRate != targetRate)
                source = new SincResamplingSampleProvider(source, targetRate);
        }

        _sampleProvider = source;
        _equalizer = new EqualizerSampleProvider(source);
        _normalize = new NormalizeSampleProvider(_equalizer);
        _volumeProvider = new VolumeSampleProvider(_normalize) { Volume = _muted ? 0f : _volume };
        return _volumeProvider;
    }

    /// <summary>Target volume-normalization gain (linear, 1 = off).
    /// The provider ramps to the target — loudness measurement arrives asynchronously.</summary>
    public void SetNormalizeGain(float linear) => _normalize?.Target = linear;

    /// <summary>
    /// NAudio raises PlaybackStopped for manual Stop()/switching too, and WasapiOut
    /// posts it to the captured SynchronizationContext — it lands AFTER the new output
    /// started. Treating those as "track ended" caused runaway auto-advance: every
    /// skip queued another Next() until the queue ran out (silent, "stuck on pause").
    /// Only the *current* output's event may auto-advance.
    /// </summary>
    private void HandleOutputStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _output)) return;
        PlaybackStopped?.Invoke(this, e);
    }

    /// <summary>
    /// Picks a reader by extension. internal for unit tests (BatPlayer.Tests).
    /// Decoder errors return null instead of throwing (logged via Logger).
    /// http(s) URLs (online streams, e.g. SoundCloud) open via Media Foundation,
    /// which can stream mp3 progressively; file readers do not apply to URLs.
    /// </summary>
    internal static WaveStream? CreateReader(string filePath)
    {
        // Online stream: MediaFoundationReader only — extension-based selection does not apply.
        if (filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new MediaFoundationReader(filePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Media Foundation stream reader failed: {filePath}");
                return null;
            }
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".mp3":
                    return new Mp3FileReader(filePath);
                case ".wav":
                    return new WaveFileReader(filePath);
                case ".aiff":
                case ".aif":
                    return new AiffFileReader(filePath);
                case ".ogg":
                case ".oga":
                    return CreateVorbisReader(filePath);
                case ".flac":
                    return CreateFlacReader(filePath);
                default:
                    // Opus, AAC, M4A, WMA, ALAC, etc. — via Media Foundation.
                    return new MediaFoundationReader(filePath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Open reader failed: {filePath}");
            return null;
        }
    }

    /// <summary>
    /// OGG Vorbis: managed NAudio.Vorbis decoder (no OS codec dependency).
    /// Fallback — Media Foundation (decodes on some systems), then null.
    /// </summary>
    private static WaveStream? CreateVorbisReader(string filePath)
    {
        // MF fallback for .ogg removed: Media Foundation cannot decode Vorbis, and a
        // "successful" open of a junk file kept a handle (leak, broke tests).
        try
        {
            return new NAudio.Vorbis.VorbisWaveReader(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Vorbis reader failed: {filePath}");
            return null;
        }
    }

    /// <summary>
    /// FLAC: managed BunLabs.NAudio.Flac decoder (outputs the file's native format —
    /// matters for bit-perfect exclusive). Fallback — Media Foundation (Windows 10+).
    /// </summary>
    private static WaveStream? CreateFlacReader(string filePath)
    {
        try
        {
            return new NAudio.Flac.FlacReader(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"FlacReader failed, trying Media Foundation: {filePath}");
            try
            {
                return new MediaFoundationReader(filePath);
            }
            catch (Exception ex2)
            {
                Logger.Error(ex2, $"Media Foundation fallback failed: {filePath}");
                return null;
            }
        }
    }

    private static MMDevice ResolveDevice(string? friendlyName)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (string.IsNullOrEmpty(friendlyName))
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            if (d.FriendlyName == friendlyName) return d;

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void Play()
    {
        if (_output != null && _output.PlaybackState != PlaybackState.Playing)
            _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
            _output.Pause();
    }

    public void Stop()
    {
        if (_output == null) return;
        // Detach first: its PlaybackStopped (posted async to the UI thread)
        // must not fire after a manual stop/skip.
        var output = _output;
        _output = null;
        output.Stop();
        output.Dispose();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader == null) return;
        var target = TimeSpan.FromSeconds(
            Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds));

        // Seeking while playing broke the decoder: the WasapiOut audio thread reads
        // the reader concurrently, and writing CurrentTime mid-read corrupted MP3
        // frame state — Read threw or returned EOF, PlaybackStopped fired, and the
        // track stopped or restarted after a timeline click. Pausing the output
        // during seek removes the concurrent read entirely; WASAPI pause/resume
        // is click-free.
        var wasPlaying = IsPlaying;
        if (wasPlaying) _output?.Pause();
        _reader.CurrentTime = target;

        // The decoder sometimes rejects the position on the first try (partial
        // frame-index shift) — retry the write once.
        if (Math.Abs((_reader.CurrentTime - target).TotalSeconds) > 0.25)
            _reader.CurrentTime = target;

        if (wasPlaying) _output?.Play();

        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEqualizerEnabled(bool enabled)
    {
        if (_equalizer != null) _equalizer.Enabled = enabled;
    }

    public void ApplyEqualizerBands(IReadOnlyList<BatPlayer.Models.EqualizerBand> bands)
    {
        if (_equalizer != null) _equalizer.ApplyBands(bands);
    }

    public void UpdateEqualizerBand(int index, BatPlayer.Models.EqualizerBand band)
    {
        if (_equalizer != null) _equalizer.UpdateBand(index, band);
    }

    public void SetEqualizerSolo(double? freqHz, double? q)
    {
        if (_equalizer != null) _equalizer.SetSolo(freqHz, q);
    }

    public void SetEqualizerPreGain(double preGainDb)
    {
        if (_equalizer != null)
            // The provider's PreGain is a LINEAR multiplier, but the value arrives
            // in dB. Without conversion 0 dB became a factor of 0 — the first EQ
            // node muted all sound.
            _equalizer.PreGain = (float)Math.Pow(10, preGainDb / 20.0);
    }

    /// <summary>Fades volume to the target over durationMs (prevents clicks).
    /// The final step lands exactly on target; the target is remembered so
    /// concurrent operations can always restore the correct volume.</summary>
    public async Task FadeVolumeAsync(float target, int durationMs = 200)
    {
        if (_volumeProvider == null) { _volume = Math.Clamp(target, 0f, 1f); return; }
        _fadeTarget = Math.Clamp(target, 0f, 1f);

        // A new fade cancels the previous one: otherwise the old loop keeps writing
        // volume (usually 0) into the NEW provider after a track change -> silence
        // until the user jiggles the volume.
        _fadeCts?.Cancel();
        _fadeCts?.Dispose();
        _fadeCts = new CancellationTokenSource();
        var token = _fadeCts.Token;

        var start = Volume;
        var steps = Math.Max(1, durationMs / 10);
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                // Intermediate steps only — the last one sets volume exactly to target.
                if (i < steps)
                    Volume = start + (_fadeTarget - start) * (i / (float)steps);
                await Task.Delay(10, token);
            }
            Volume = _fadeTarget;
        }
        catch (OperationCanceledException)
        {
            // Fade cancelled (new track/operation started) — the new owner sets the volume.
        }
    }

    /// <summary>Immediately stops the active volume fade (before opening a new track).</summary>
    public void CancelFade()
    {
        _fadeCts?.Cancel();
    }

    private void DisposeReader()
    {
        _reader?.Dispose();
        _reader = null;
        _sampleProvider = null;
        _equalizer = null;
        _volumeProvider = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeReader();
        _endpointVolume?.Dispose();
    }
}
