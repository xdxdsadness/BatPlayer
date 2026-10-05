using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using BatPlayer.Localization;
using BatPlayer.Services;

namespace BatPlayer.Audio;

/// <summary>
/// Низкоуровневый аудиодвижок на NAudio. Отвечает за:
/// - чтение файла (Mp3FileReader, WaveFileReader, AiffFileReader, VorbisWaveReader для OGG,
///   FlacReader для FLAC, MediaFoundationReader — fallback для всего остального);
/// - вывод через WasapiOut (shared по умолчанию, exclusive опционально);
/// - адаптацию формата под устройство (каналы + ресемплинг в MixFormat) — трек не должен
///   «не играть» из-за несовпадения формата файла с форматом устройства;
/// - цепочку эффектов: (адаптация) -> Equalizer -> VolumeSampleProvider -> Output.
/// Не содержит логики плейлистов — только текущий трек.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    // Event-sync режим позволяет более низкую задержку без глитчей;
    // 120мс — запас от тресков при пиках UI/CPU (скролл, декоды обложек):
    // на 80мс слышны dropout'ы как «шум поверх» трека.
    private const int SharedLatencyMs = 120;
    // Exclusive требовательнее к размеру буфера (выравнивание по периоду устройства),
    // оставляем проверенное значение: неуспешная инициализация = потеря bit-perfect режима.
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

    // Логическая громкость и флаг мьюта хранятся отдельно от громкости провайдера:
    // раньше IsMuted выводился из «Volume == 0», из-за чего громкость при мьюте
    // сохранялась как 0 и после снятия мьюта звук не возвращался.
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

    /// <summary>Список доступных WASAPI-устройств вывода.</summary>
    public static IEnumerable<MMDevice> EnumerateDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    /// <summary>Открыть файл и подготовить к воспроизведению. Не запускает автоматически.</summary>
    public void Open(string filePath, bool useExclusiveMode, string? deviceFriendlyName = null)
    {
        Stop();
        DisposeReader();

        _reader = CreateReader(filePath);
        if (_reader == null)
            throw new NotSupportedException($"{Loc.Get("ErrorUnsupportedFormat")}: {filePath}");

        var device = ResolveDevice(deviceFriendlyName);

        // Exclusive: bit-perfect попытка с нативным форматом файла (без ресемплинга
        // и ремапа каналов). Устройство занято или формат не поддерживается -> падаем
        // в shared с адаптацией: трек не должен «не играть» из-за формата.
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
                // Цепочка exclusive-попытки больше не используется.
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
    /// Bit-perfect цепочка для exclusive-режима: нативный формат файла без адаптации.
    /// EQ работает на исходной частоте — ресемплинга нет, частоты полос корректны.
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
    /// Shared-цепочка с адаптацией под микс-формат устройства:
    /// reader -> адаптер каналов -> ресемплинг (WDL) -> Equalizer -> Volume.
    /// EQ стоит ПОСЛЕ ресемплинга: частоты полос должны соответствовать
    /// реальной выходной частоте, а не частоте файла.
    /// </summary>
    private ISampleProvider BuildSharedChain(WaveStream reader, MMDevice device)
    {
        var source = reader.ToSampleProvider();
        var sourceFormat = source.WaveFormat;

        int targetRate;
        int targetChannels;
        try
        {
            // AudioClient.MixFormat — целевой формат сеанса shared-режима устройства.
            using var audioClient = device.AudioClient;
            targetRate = audioClient.MixFormat.SampleRate;
            targetChannels = audioClient.MixFormat.Channels;
        }
        catch (Exception ex)
        {
            // Нет доступа к MixFormat (редкие драйверы) — играем в формате файла,
            // WASAPI shared сам приведёт формат, если сможет.
            Logger.Error(ex, "Failed to read device MixFormat, using source format");
            targetRate = sourceFormat.SampleRate;
            targetChannels = sourceFormat.Channels;
        }

        if (targetChannels >= 1 && targetRate > 0)
        {
            // Адаптация каналов (частные случаи — штатные NAudio-провайдеры).
            if (sourceFormat.Channels == 1 && targetChannels == 2)
                source = new MonoToStereoSampleProvider(source);
            else if (sourceFormat.Channels == 2 && targetChannels == 1)
                source = new StereoToMonoSampleProvider(source);
            else if (sourceFormat.Channels == 1 && targetChannels > 2)
                source = new ChannelMappingSampleProvider(new MonoToStereoSampleProvider(source), targetChannels);
            else if (sourceFormat.Channels != targetChannels)
                source = new ChannelMappingSampleProvider(source, targetChannels);

            // Ресемплинг под частоту микс-формата: sinc-режим WDL (штатный провайдер
            // NAudio работает в режиме линейной интерполяции и глушит верхние частоты —
            // см. SincResamplingSampleProvider).
            if (source.WaveFormat.SampleRate != targetRate)
                source = new SincResamplingSampleProvider(source, targetRate);
        }

        _sampleProvider = source;
        _equalizer = new EqualizerSampleProvider(source);
        _normalize = new NormalizeSampleProvider(_equalizer);
        _volumeProvider = new VolumeSampleProvider(_normalize) { Volume = _muted ? 0f : _volume };
        return _volumeProvider;
    }

    /// <summary>Целевое усиление нормализации громкости (линейное, 1 — выкл).
    /// Провайдер доезжает до цели рампой — измерение громкости приезжает асинхронно.</summary>
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
    /// Выбор reader'а по расширению. internal — для юнит-тестов (BatPlayer.Tests).
    /// Ошибка декодера не бросает исключение наружу: возвращает null (логи в Logger).
    /// http(s)-ссылки (онлайн-стримы, напр. SoundCloud) открываются через Media Foundation
    /// — он умеет читать mp3-прогресс из сети; file-ридеры для URL неприменимы.
    /// </summary>
    internal static WaveStream? CreateReader(string filePath)
    {
        // Онлайн-стрим: только MediaFoundationReader, выбор по расширению не имеет смысла.
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
                    // Opus, AAC, M4A, WMA, ALAC и прочее — через Media Foundation.
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
    /// OGG Vorbis: управляемый декодер NAudio.Vorbis (не зависит от кодеков ОС).
    /// Fallback — Media Foundation (на некоторых системах декодирует сам), затем null.
    /// </summary>
    private static WaveStream? CreateVorbisReader(string filePath)
    {
        // MF-fallback для .ogg убран: Media Foundation не декодирует Vorbis, а
        // «успешное» открытие мусорного файла держало хендл (утечка, ломало тесты).
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
    /// FLAC: управляемый декодер BunLabs.NAudio.Flac (собственный выход в формате файла —
    /// важно для bit-perfect exclusive). Fallback — Media Foundation (нативный в Windows 10+).
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

        // Перемотка «на живую» ломала декодер: аудио-поток WasapiOut параллельно
        // читает reader, и запись CurrentTime посреди чтения рвала внутреннее
        // состояние MP3-фреймов — Read кидался/возвращал конец, срабатывал
        // PlaybackStopped, и трек после клика по таймлайну останавливался или
        // перезапускался с начала. Пауза вывода на время перемотки убирает
        // параллельное чтение полностью; Pause/Resume WASAPI делает без щелчков.
        var wasPlaying = IsPlaying;
        if (wasPlaying) _output?.Pause();
        _reader.CurrentTime = target;

        // Верификация: декодер иногда не принимает позицию с первого раза
        // (частичный сдвиг фрейм-индекса) — повторяем запись один раз.
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
            // PreGain провайдера — ЛИНЕЙНЫЙ множитель, а приходит значение в дБ.
            // Без конверсии 0 дБ превращался в множитель 0 — первая же точка
            // эквалайзера полностью глушила звук.
            _equalizer.PreGain = (float)Math.Pow(10, preGainDb / 20.0);
    }

    /// <summary>Плавно изменить громкость за durationMs (защита от щелчков).
    /// Последний шаг точно попадает в цель; цель запоминается, чтобы параллельные
    /// операции всегда могли восстановить корректную громкость.</summary>
    public async Task FadeVolumeAsync(float target, int durationMs = 200)
    {
        if (_volumeProvider == null) { _volume = Math.Clamp(target, 0f, 1f); return; }
        _fadeTarget = Math.Clamp(target, 0f, 1f);

        // Новый фейд отменяет предыдущий: иначе старый цикл продолжает писать
        // громкость (обычно 0) уже в НОВЫЙ провайдер после смены трека -> тишина,
        // «лечится» только дёрганием громкости.
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
                // Промежуточные шаги — без последнего: он ставит громкость ровно в цель.
                if (i < steps)
                    Volume = start + (_fadeTarget - start) * (i / (float)steps);
                await Task.Delay(10, token);
            }
            Volume = _fadeTarget;
        }
        catch (OperationCanceledException)
        {
            // Фейд отменён (начался новый трек/операция) — громкость выставит новый владелец.
        }
    }

    /// <summary>Немедленно остановить активный фейд громкости (перед Open нового трека).</summary>
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
