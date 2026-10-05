using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.Audio;

/// <summary>
/// Высокоуровневый аудио-сервис: очередь, переходы, shuffle/repeat, сохранение состояния.
/// Используется ViewModels. Внутри использует AudioEngine.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly EqualizerService _equalizer;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly HistoryService _history;

    private readonly ObservableCollection<Track> _queue = new();
    public IReadOnlyList<Track> Queue => _queue;

    private int _queueIndex = -1;
    private bool _shuffle;
    private RepeatMode _repeat = RepeatMode.None;
    private List<int>? _shuffleOrder;
    private int _shufflePos = -1;

    public Track? CurrentTrack => (_queueIndex >= 0 && _queueIndex < _queue.Count) ? _queue[_queueIndex] : null;

    public bool IsPlaying => _engine.IsPlaying;
    public bool IsPaused  => _engine.IsPaused;

    // ===== Контекст очереди: плейлист, из которого играет музыка =====
    // Заполняется только PlayTrack-ом ИЗ плейлиста (PlaylistViewModel): карточка
    // плейлиста по нему показывает Pause вместо Play и держит оверлей, как
    // карточка играющего трека. Любой другой PlayTrack (карточка библиотеки,
    // волна, поиск...) сбрасывает контекст — очередь больше не плейлист.
    private long? _playlistId;

    /// <summary>Id плейлиста, чья очередь сейчас играет; null — очередь не из плейлиста.</summary>
    public long? CurrentPlaylistId => _playlistId;

    /// <summary>Сменился источник очереди (плейлист ↔ не-плейлист).</summary>
    public event EventHandler<long?>? QueueSourceChanged;

    private void SetPlaylistContext(long? playlistId)
    {
        if (_playlistId == playlistId) return;
        _playlistId = playlistId;
        QueueSourceChanged?.Invoke(this, playlistId);
    }

    public TimeSpan Position => _engine.CurrentTime;
    public TimeSpan Duration => _engine.TotalTime;

    // Пользовательская (целевая) громкость 0..100. Хранится ОТДЕЛЬНО от движка:
    // фейды пишут в движок 0 (fade-out при смене трека), но не должны влиять на
    // целевую громкость — иначе новый трек стартует беззвучно («лечится» только
    // дёрганием ползунка).
    private double _userVolume = 50;

    public double Volume // 0..100, double — без округления при перетаскивании слайдера
    {
        get => _userVolume;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (Math.Abs(v - _userVolume) < 0.001) return;
            _userVolume = v;
            _engine.Volume = IsMuted ? 0f : (float)(v / 100.0);
            VolumeChanged?.Invoke(this, v);
        }
    }

    public bool IsMuted
    {
        get => _engine.IsMuted;
        set { _engine.IsMuted = value; MuteChanged?.Invoke(this, value); }
    }

    public bool IsShuffle
    {
        get => _shuffle;
        set { _shuffle = value; RebuildShuffleOrder(); ShuffleChanged?.Invoke(this, value); }
    }

    public RepeatMode RepeatMode
    {
        get => _repeat;
        set { _repeat = value; RepeatModeChanged?.Invoke(this, value); }
    }

    /// <summary>
    /// Резолв локального пути для треков без файла (SC-runtime-карточки: FilePath пуст,
    /// Source="soundcloud", ScId задан). Вызывается в PlayInternalAsync перед Open;
    /// null — файл получить не удалось (авто-переход: трек пропускается; клик
    /// пользователя: Stop + ошибка без перескока — ResolveFailurePolicy.Decide).
    /// Назначается в MainViewModel (SoundCloudService + дисковый кэш стримов).
    /// </summary>
    public Func<Track, CancellationToken, Task<string?>>? FilePathResolver { get; set; }

    public event EventHandler<Track?>? CurrentTrackChanged;
    public event EventHandler? PlayStateChanged;

    /// <summary>Очередь доиграла до конца (естественное окончание последнего трека,
    /// без RepeatAll/RepeatOne и без ошибок воспроизведения). Аргумент — последний
    /// игравший трек. «Волна» использует это для авто-обновления микса.</summary>
    public event EventHandler<Track?>? QueueEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<bool>? MuteChanged;
    public event EventHandler<bool>? ShuffleChanged;
    public event EventHandler<RepeatMode>? RepeatModeChanged;
    public event EventHandler<string>? ErrorOccurred;

    private System.Threading.Timer? _positionTimer;
    // The timer callback runs on thread-pool threads; capture the UI dispatcher
    // up front — Dispatcher.CurrentDispatcher inside the callback would create
    // (and strand) a new Dispatcher per pool thread, so notifications got lost.
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    // Длительность fade на стыках треков (~100-120мс): убирает щелчки при
    // переключении/остановке, переход остаётся быстрым.
    private const int FadeTransitionMs = 300;
    private const int FadeOutClickMs = 140;
    private const int FadeInMs = 320;
    // Счётчик операций play/stop: устаревшие асинхронные fade-цепочки
    // (быстрое переключение подряд, play сразу после fade-stop) отменяются.
    private int _opSeq;
    // Подряд неуспешные резолвы FilePathResolver: защита от зацикливания Next()
    // на неиграбельных SC-треках (гео/HLS-only). Политика обработки неудачи
    // (клик пользователя — ошибка без перескока; авто-переход — скип с лимитом
    // подряд) — ResolveFailurePolicy.Decide.
    private int _unresolvableStreak;
    // Гард от повторного входа префетча: не чаще одного фонового резолва
    // следующего трека за раз (быстрые переключения подряд не должны плодить
    // параллельных скачиваний).
    private int _prefetchInFlight;

    // ===== Засчитывание прослушки (статистика) =====
    // Прослушка пишется не в момент нажатия Play, а ПОСЛЕ прослушивания: трек
    // дослушан до конца ИЛИ реально прослушано >= 15 секунд (для коротких треков
    // < 60 c — половина длительности). Переключил трек в первые секунды —
    // прослушка не засчитывается.
    private static readonly TimeSpan MinListened = TimeSpan.FromSeconds(15);
    private const double ShortTrackFraction = 0.5;
    private static readonly TimeSpan ShortTrackCutoff = TimeSpan.FromSeconds(60);

    // Трек, чья прослушка «в ожидании» (сейчас играет), и точка старта.
    private Track? _pendingListenTrack;
    private long _pendingListenStartTicks;
    private bool _pendingListenReachedEnd;
    // Снимок на момент ухода с трека (позиция движка после Stop уже невалидна).
    private (Track Track, long ListenedTicks, bool ReachedEnd)? _capturedListen;

    // ===== Состояние эквалайзера =====
    // EqualizerSampleProvider пересоздаётся при каждом Open: набор полос (частота,
    // усиление, тип, крутизна) и preamp хранятся здесь и заново применяются к новому
    // провайдеру, иначе эквалайзер сбрасывался при каждом переключении трека.
    private readonly List<EqualizerBand> _eqBands = new();
    private double _eqPreGain;
    private double? _soloFreq;
    private double? _soloQ;

    public AudioService(AudioEngine engine, EqualizerService equalizer, SettingsService settings,
                        LibraryService library, HistoryService history)
    {
        _engine = engine;
        _equalizer = equalizer;
        _settings = settings;
        _library = library;
        _history = history;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        _engine.PlaybackStopped += OnPlaybackStopped;

        _positionTimer = new System.Threading.Timer(_ =>
        {
            // На паузе позиция не меняется — тики только гоняют UI-конвейер
            // (BeginInvoke → подписчики → перерисовки) впустую. Пропускаем: всё
            // состояние паузы/старта и так приходит через PlayStateChanged.
            if (!_engine.IsPlaying) return;

            // Позиция читается НА UI-ПОТОКЕ, а не на потоке таймера: иначе тик,
            // заснувший в очереди до клика/перемотки, доставляет СТАРУЮ позицию
            // ПОСЛЕ seek'а — guard перемотки пропускает её (для seek'а назад
            // «p >= цель» истинно и для старой позиции), и ползунок откатывается
            // назад, не доехав до точки клика. Чтение здесь всегда видит движок
            // ПОСЛЕ всех предыдущих UI-операций.
            _dispatcher.BeginInvoke(() => PositionChanged?.Invoke(this, _engine.CurrentTime));
        }, null, 100, 100);
    }

    /// <summary>Загрузить сохранённое состояние (вызывать на старте после загрузки библиотеки).
    /// openPlayback=false — пересоздание окна: очередь и UI восстанавливаются, но играющий
    /// трек НЕ переоткрывается (аудио-сервис живёт на уровне приложения и уже играет).</summary>
    public async Task RestoreStateAsync(bool openPlayback = true)
    {
        var state = await _library.LoadPlaybackStateAsync();
        if (state == null) return;

        if (!openPlayback)
        {
            // Пересоздание окна: сервис уже живой — громкость/режимы и очередь
            // перечитывать из сохранённого снапшота нельзя (снапшот устарел, а
            // треки ДОБАВЛЯЛИСЬ к живой очереди — список удваивался). Новому
            // окну нужен только текущий трек и состояние play/pause для его VM;
            // остальные значения VM возьмёт из живых свойств сервиса.
            CurrentTrackChanged?.Invoke(this, CurrentTrack);
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var s = _settings.Current;

        Volume = state.Volume;
        IsMuted = state.IsMuted;
        IsShuffle = state.IsShuffle;
        RepeatMode = state.RepeatMode;

        if (state.QueueTrackIds.Count > 0)
        {
            var tracks = await _library.GetTracksByIdsAsync(state.QueueTrackIds);
            foreach (var t in tracks) _queue.Add(t);
            // Guard: очередь может оказаться пустой (в сохранённом состоянии были только
            // SC/VK-треки, которых в таблице tracks нет) — Clamp(0, -1) ронял запуск.
            if (_queue.Count > 0)
                _queueIndex = Math.Clamp(state.QueueIndex, 0, _queue.Count - 1);
            // Shuffle-порядок строится при установке IsShuffle, когда очередь ещё ПУСТА
            // (восстановление начинается с настроек) — пересобираем на загруженную очередь,
            // иначе _shuffleOrder оставался null и первый автопереход падал с NRE.
            RebuildShuffleOrder();
        }
        else if (state.CurrentTrackId is long id)
        {
            var t = (await _library.GetTracksByIdsAsync(new[] { id })).FirstOrDefault();
            if (t != null) { _queue.Add(t); _queueIndex = 0; }
        }

        if (s.EqualizerEnabled && !string.IsNullOrEmpty(s.CurrentEqualizerPreset))
        {
            var preset = _equalizer.FindPreset(s.CurrentEqualizerPreset);
            if (preset != null) ApplyEqualizerPreset(preset);
        }

        CurrentTrackChanged?.Invoke(this, CurrentTrack);

        if (openPlayback && s.AutoResumePlayback && CurrentTrack != null)
        {
            try
            {
                // Restore track + position but stay paused — no surprise audio on startup.
                _engine.Open(CurrentTrack.FilePath, s.UseWasapiExclusive, s.AudioOutputDevice);
                _engine.SetEqualizerEnabled(s.EqualizerEnabled);
                _ = ApplyNormalizationAsync(CurrentTrack.FilePath);
                _engine.Volume = IsMuted ? 0f : (float)(_userVolume / 100.0);
                if (state.LastPositionTicks > 0)
                    _engine.Seek(TimeSpan.FromTicks(state.LastPositionTicks));
                    // Синхронизация VM: движок перемотан на сохранённую позицию,
                    // VM обязана узнать её — иначе таймлайн показывал 00:00 и
                    // любой клик по линии «откатывался» к нулю.
                    PositionChanged?.Invoke(this, _engine.CurrentTime);
                PlayStateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                ErrorOccurred?.Invoke(this, Loc.Get("ErrorRestoreTrack"));
            }
        }
    }

    /// <param name="playlistId">Id плейлиста, если очередь — его треки (клик по карточке
    /// плейлиста / двойной клик по строке деталей). Все остальные вызовы не передают его —
    /// контекст плейлиста сбрасывается.</param>
    public void PlayTrack(Track track, IEnumerable<Track>? contextQueue = null, long? playlistId = null)
    {
        // Помним, что реально звучит сейчас: если резолв нового трека упадёт,
        // указатель «текущего» вернём на звучащий трек — иначе мёртвая карточка
        // остаётся CurrentTrack, и повторный клик по ней тогглит паузу старого
        // (жалоба: «пауза ставится, включается прошлый трек»).
        var audibleTrack = CurrentTrack;
        SetPlaylistContext(playlistId);
        _queue.Clear();
        if (contextQueue != null)
            foreach (var t in contextQueue) _queue.Add(t);
        else
            _queue.Add(track);

        _queueIndex = _queue.IndexOf(track);
        if (_queueIndex < 0) { _queue.Add(track); _queueIndex = _queue.Count - 1; }

        // Явный клик по карточке: платформенный трек (SoundCloud/VK), проваливший резолв
        // ранее в этой сессии (IsAvailable=false), пробуем снова — сеть/VPN могли вернуться.
        // Объект тот же, что лежит в списке VM/очереди, поэтому сброс флага виден и
        // авто-переходам.
        if (track.IsPlatformTrack && !track.IsAvailable)
            track.IsAvailable = true;

        RebuildShuffleOrder();
        PlayWithFade(resumeFromLast: true, userInitiated: true, audibleTrack);
    }

    public void SetQueue(IEnumerable<Track> tracks, int startIndex = 0)
    {
        _queue.Clear();
        foreach (var t in tracks) _queue.Add(t);
        _queueIndex = Math.Clamp(startIndex, 0, Math.Max(0, _queue.Count - 1));
        RebuildShuffleOrder();
    }

    public void PlayNext(Track track)
    {
        if (_queueIndex < 0) { PlayTrack(track); return; }
        _queue.Insert(_queueIndex + 1, track);
    }

    public void AddToQueue(Track track) => _queue.Add(track);

    public void RemoveFromQueue(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _queue.RemoveAt(index);
        if (index < _queueIndex) _queueIndex--;
        else if (index == _queueIndex) Stop();
    }

    public void ClearQueue()
    {
        Stop();
        SetPlaylistContext(null);
        _queue.Clear();
        _queueIndex = -1;
        CurrentTrackChanged?.Invoke(this, null);
    }

    public void MoveQueueItem(int from, int to)
    {
        if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count) return;
        var item = _queue[from];
        _queue.RemoveAt(from);
        _queue.Insert(to, item);
        if (_queueIndex == from) _queueIndex = to;
        else if (from < _queueIndex && to >= _queueIndex) _queueIndex--;
        else if (from > _queueIndex && to <= _queueIndex) _queueIndex++;
    }

    public void Play()
    {
        // Play() достижим только через PlayPauseToggle (кнопка/хоткей/трей/повторный
        // клик по карточке текущего трека) — всегда пользовательское намерение, не
        // авто-переход: мёртвый SC-трек ретраим, а не скипаем.
        if (CurrentTrack == null && _queue.Count > 0) { _queueIndex = 0; PlayWithFade(false, userInitiated: true); return; }
        if (CurrentTrack != null && !_engine.IsPlaying && _engine.IsPaused) { _engine.Play(); PlayStateChanged?.Invoke(this, EventArgs.Empty); return; }
        if (CurrentTrack != null) PlayWithFade(false, userInitiated: true);
    }

    public void Pause()
    {
        _engine.Pause();
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PlayPauseToggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Stop()
    {
        // Уход с трека: фиксируем прослушанное до остановки движка, засчитываем по порогу.
        SnapshotPendingListen();
        _ = CommitCapturedListenAsync();

        // Smooth fade-stop: короткий fade-out, затем Stop. UI не блокируем —
        // переход асинхронный; PlayStateChanged шлём после фактической остановки,
        // чтобы иконка не «мигнула» обратно, пока звук ещё догорает.
        if (_settings.Current.SmoothVolumeChanges && _engine.IsPlaying)
        {
            int seq = ++_opSeq;
            _ = FadeStopAsync(seq);
            return;
        }

        _opSeq++;
        _engine.Stop();
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // === Нормализация громкости ===
    // Целевой уровень в RMS dBFS: современные стриминги держат ~-14…-16 LUFS,
    // что для RMS-прокси соответствует примерно -16 dBFS. Диапазон усиления
    // ограничен ±9 дБ: слишком тихий источник подтягиваем, слишком громкий — гасим.
    private const double NormalizeTargetDb = -16.0;
    private const double NormalizeMaxGainDb = 9.0;

    /// <summary>Применить нормализацию к открытому файлу (fire-and-forget): RMS-громкость
    /// считается попутно с волной (WaveformCache, тот же декод) и приезжает через
    /// секунды после старта — усиление доезжает рампой провайдера, без щелчка.
    /// Последовательный номер (_opSeq) страхует от применения к уже закрытому треку.</summary>
    private async Task ApplyNormalizationAsync(string filePath)
    {
        if (!_settings.Current.NormalizeVolume) return;
        int seq = _opSeq;
        try
        {
            var rmsDb = await WaveformCache.GetLoudnessDbAsync(filePath);
            if (seq != _opSeq) return; // трек уже сменился — усиление не наше
            if (rmsDb is null)
            {
                _engine.SetNormalizeGain(1f);
                return;
            }
            var gainDb = Math.Clamp(NormalizeTargetDb - rmsDb.Value, -NormalizeMaxGainDb, NormalizeMaxGainDb);
            Logger.Info($"[NORM] {Path.GetFileName(filePath)}: rms={rmsDb:0.0} dBFS -> gain {gainDb:+0.0;-0.0} dB");
            _engine.SetNormalizeGain((float)Math.Pow(10, gainDb / 20.0));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Volume normalization failed");
        }
    }

    /// <summary>
    /// Fade-in нового трека до целевой громкости (fire-and-forget). Движок сам ставит
    /// целевую громкость в конце фейда; переключение на другой трек отменяет фейд
    /// через CancelFade перед Open следующего — громкость выставит новый владелец.
    /// На сбое громкость восстанавливается вручную, пока операция ещё актуальна.
    /// </summary>
    private async Task FadeInNewTrackAsync(float targetVolume, int seq)
    {
        try
        {
            await _engine.FadeVolumeAsync(targetVolume, FadeInMs);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fade-in failed");
        }
        finally
        {
            // Финал фейда синхронизируем с текущим состоянием громкости: за 200 мс
            // пользователь мог подвинуть ползунок/нажать mute — побеждает ползунок.
            if (seq == _opSeq)
                _engine.Volume = (IsMuted || _userVolume <= 0) ? 0f : (float)(_userVolume / 100.0);
        }
    }

    private async Task FadeStopAsync(int seq)
    {        try
        {
            await _engine.FadeVolumeAsync(0f, FadeTransitionMs);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        if (seq != _opSeq) return; // началась новая операция (play/next) — не глушим новый трек
        _engine.CancelFade();
        _engine.Stop();
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Переключение трека: fade-out старого (~70мс, если включены smooth-переходы),
    /// затем Open → громкость → Play нового. Fade-in сознательно убран: новый трек
    /// стартует сразу с целевой громкостью — тихий старт («нет звука, пока не покрутишь
    /// громкость») был следствием fade-цепочки, обрывавшейся между Play() и
    /// восстановлением громкости.
    /// Всё на UI-контексте (await не блокирует UI), при быстром переключении
    /// устаревшая цепочка отменяется через _opSeq.
    /// userInitiated — клик пользователя (карточка/Play) против авто-перехода
    /// (Next/конец трека): определяет политику неудачного резолва (ResolveFailurePolicy).
    /// </summary>
    private async void PlayWithFade(bool resumeFromLast, bool userInitiated, Track? audibleTrack = null)
    {
        int seq = ++_opSeq;
        // Fade-out перенесён ВНУТРЬ PlayInternalAsync — ПОСЛЕ резолва нового трека.
        // Раньше громкость гасилась до обращения к сети, и на время резолва (1-3 с)
        // наступала тишина; упавший резолв оставлял громкость в нуле — выглядело как
        // «клик по новому треку поставил паузу текущему».
        await PlayInternalAsync(resumeFromLast, seq, userInitiated, audibleTrack);
    }

    private async Task PlayInternalAsync(bool resumeFromLast, int seq, bool userInitiated, Track? audibleTrack = null)
    {
        if (CurrentTrack == null) return;

        // Уходим с предыдущего трека: снимаем его прослушку (движок ещё на старом
        // файле) и, если порог пройден, засчитываем. Позиция невалидна после Open.
        SnapshotPendingListen();
        await CommitCapturedListenAsync();

        try
        {
            var s = _settings.Current;

            var filePath = CurrentTrack.FilePath;
            // SC-runtime-карточки приходят с пустым FilePath: файл (локальный матч или mp3
            // из кэша/сети) резолвится здесь, внутри той же операции seq — переключение
            // трека во время ожидания сети отменяет устаревшую цепочку через _opSeq.
            if (SoundCloudRuntimeTracks.NeedsFilePathResolve(CurrentTrack) && FilePathResolver != null)
            {
                // Трек уже провалил резолв в этой сессии (IsAvailable=false).
                // Авто-переход: сеть не дёргаем повторно — сразу пропускаем, переход
                // остаётся мгновенным. Пользовательский клик (маркер сброшен в
                // PlayTrack/Play) — ретраим: сеть или VPN могли вернуться.
                if (!CurrentTrack.IsAvailable && !userInitiated)
                {
                    Logger.Warn($"Track known unavailable (scId={CurrentTrack.ScId}) — skipping");
                    SkipUnresolvable();
                    return;
                }

                var resolved = await FilePathResolver(CurrentTrack, CancellationToken.None);
                if (seq != _opSeq) return; // пока резолвили, переключились на другой трек

                if (string.IsNullOrEmpty(resolved))
                {
                    // Трек недоступен (не streamable / гео / сеть упала): помечаем в
                    // runtime-объекте, чтобы авто-переходы не повторяли резолв в этой
                    // сессии. Дальше — по политике ResolveFailurePolicy: клик пользователя
                    // → чистая ошибка без перескока; авто-переход → пропуск, число подряд
                    // ограничено (иначе Next() зациклится на мёртвой очереди, RepeatAll).
                    CurrentTrack.IsAvailable = false;
                    Logger.Warn($"Track file resolve failed (scId={CurrentTrack.ScId}) — userInitiated={userInitiated}");
                    if (ResolveFailurePolicy.Decide(userInitiated, _unresolvableStreak) == ResolveFailureAction.StopWithError)
                    {
                        // Детерминированный UX: кликнул → играет ИЛИ чистая ошибка, без
                        // перескоков. Open для нового трека ещё не вызывался, старый НЕ
                        // глушился (fade теперь после резолва) — он продолжает играть,
                        // пользователю достаточно тоста об ошибке.
                        _unresolvableStreak = 0;
                        // Возврат «текущего» на реально звучащий трек: без этого карточка
                        // мёртвого трека остаётся CurrentTrack (а играет старый) — повторный
                        // клик по ней выглядит как «клик по текущему» и ставит старый на паузу,
                        // следующий — возобновляет его же.
                        var failedTrack = CurrentTrack;
                        if (userInitiated)
                        {
                            var audibleIdx = audibleTrack == null ? -1 : _queue.IndexOf(audibleTrack);
                            if (audibleIdx < 0 && audibleTrack != null)
                            {
                                // Звучащий трек не из этой очереди (клик в другом контексте):
                                // вставляем в начало, чтобы «текущее» честно указывало на него.
                                _queue.Insert(0, audibleTrack);
                                audibleIdx = 0;
                            }
                            _queueIndex = audibleIdx; // -1 — до клика ничего не играло
                            if (_shuffle) RebuildShuffleOrder();
                            CurrentTrackChanged?.Invoke(this, CurrentTrack);
                        }
                        PlayStateChanged?.Invoke(this, EventArgs.Empty);
                        // ВАЖНО: источник берём с failedTrack (до отката), CurrentTrack здесь
                        // уже может быть null — раньше это давало NRE вместо ошибки.
                        ErrorOccurred?.Invoke(this, Loc.Get(UnavailableMessageKey(failedTrack.Source)));
                        return;
                    }
                    SkipUnresolvable();
                    return;
                }
                _unresolvableStreak = 0;
                filePath = resolved;
                // Успех возвращает кликабельность: IsAvailable мог остаться false от
                // прежней неудачной попытки — теперь повторный клик по карточке снова
                // играет этот трек.
                CurrentTrack.IsAvailable = true;
                // Запоминаем в runtime-объекте: повторный клик по карточке даёт паузу/
                // возобновление, а переходы и «добавить в очередь» видят готовый файл.
                CurrentTrack.FilePath = resolved;
            }

            // Новый трек готов (файл на руках): гасим старый ДО Open. Open подменяет
            // источник вывода и обрывает текущий звук мгновенно — параллельный
            // fade-out при этом не слышен вовсе (клик звучал как обрыв). Формула
            // перехода: fade-out старого → короткий Open → fade-in нового.
            if (s.SmoothVolumeChanges && _engine.IsPlaying)
            {
                // Клик пользователя — быстрый отклик (короткий fade-out), авто-переход
                // (кончился трек/Next) — длиннее и музыкальнее.
                var fadeOutMs = userInitiated ? FadeOutClickMs : FadeTransitionMs;
                try
                {
                    await _engine.FadeVolumeAsync(0f, fadeOutMs);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                if (seq != _opSeq) return; // переключились во время fade
            }

            _engine.CancelFade();
            _engine.Open(filePath, s.UseWasapiExclusive, s.AudioOutputDevice);
            _engine.SetEqualizerEnabled(s.EqualizerEnabled);
            // EQ пересоздан при Open: применяем сохранённые полосы и preamp.
            _engine.SetEqualizerPreGain(_eqPreGain);
            _engine.ApplyEqualizerBands(_eqBands);
            _engine.SetEqualizerSolo(_soloFreq, _soloQ);
            // Нормализация громкости: усиление под конкретный файл считается в фоне
            // (RMS из того же декода, что и волна) и доезжает рампой, без щелчка.
            _ = ApplyNormalizationAsync(filePath);

            if (resumeFromLast && CurrentTrack.LastPositionTicks > 0)
                _engine.Seek(CurrentTrack.LastPosition);

            // Порядок строго: Open → громкость → Play. При плавных переходах новый трек
            // стартует с нуля и поднимается fade-in'ом (раньше был только fade-out
            // старого — переключение звучало как резкий обрыв); без плавности громкость
            // применяется ДО старта — трек не может начаться беззвучно.
            _engine.CancelFade(); // фейд старого трека не должен писать громкость в новый провайдер
            var targetVolume = (IsMuted || _userVolume <= 0) ? 0f : (float)(_userVolume / 100.0);
            var fadeIn = s.SmoothVolumeChanges && targetVolume > 0f;
            _engine.Volume = fadeIn ? 0f : targetVolume;
            _engine.Play();
            if (fadeIn)
                _ = FadeInNewTrackAsync(targetVolume, seq);
            else
                _engine.Volume = targetVolume; // вторая гарантия: повторно применяем громкость ПОСЛЕ Play

            // Фолбэк-файл (YouTube) живёт один трек: скачали → сыграли → при переходе
            // на следующий удаляем, чтобы кэш не разрастался. FilePath у старого трека
            // сбрасываем — повторное включение снова пройдёт через фолбэк.
            if (audibleTrack != null && !ReferenceEquals(audibleTrack, CurrentTrack)
                && audibleTrack.FilePath is string oldFile && YtFallbackService.OwnsFile(oldFile))
            {
                try
                {
                    File.Delete(oldFile);
                    audibleTrack.FilePath = "";
                }
                catch { /* файл мог быть удержан — удалится при следующем заходе или старте */ }
            }
            Logger.Info($"PlayInternal: target={targetVolume:0.00} engine={_engine.Volume:0.00} src={CurrentTrack.Source} file={(SoundCloudRuntimeTracks.NeedsFilePathResolve(CurrentTrack) ? "resolved" : CurrentTrack.FilePath)}");

            // Синхронизация VM: трек мог стартовать с сохранённой позиции
            // (resumeFromLast) — таймлайн должен показать её сразу.
            PositionChanged?.Invoke(this, _engine.CurrentTime);

            CurrentTrackChanged?.Invoke(this, CurrentTrack);
            PlayStateChanged?.Invoke(this, EventArgs.Empty);

            // Прослушка «в ожидании»: засчитается после достаточного прослушивания
            // (см. SnapshotPendingListen/CommitCapturedListenAsync) — при переходе
            // к следующему треку, естественном окончании или Stop.
            _pendingListenTrack = CurrentTrack;
            _pendingListenStartTicks = _engine.CurrentTime.Ticks;
            _pendingListenReachedEnd = false;

            // Префетч следующего платформенного трека очереди (SC/VK/Яндекс Музыка):
            // mp3 докачивается в кэш платформы фоном, к моменту ручного переключения
            // трек стартует без лага скачивания.
            PrefetchNextPlatformTrack();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            // Не оставляем громкость в нуле от неудавшегося переключения: старый трек
            // мог остаться загружен в движке — возвращаем ему громкость.
            if (_engine.IsPlaying)
            {
                _engine.CancelFade();
                _engine.Volume = (IsMuted || _userVolume <= 0) ? 0f : (float)(_userVolume / 100.0);
            }
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
        }
    }

    /// <summary>
    /// Фоновая предзагрузка следующего платформенного трека очереди (SC/VK/Яндекс Музыка)
    /// при успешном старте трека: к моменту ручного переключения файл уже в кэше — переход
    /// без лага. Кандидат — чистая функция GetNextPlatformCandidate (строго текущий+1).
    /// Файл добывает общий FilePathResolver (тот же, что играет треки) — путь
    /// результата не нужен, важна сама докачка в кэш платформы.
    /// Потоки: вызов из PlayInternalAsync (UI-контекст) — кандидат вычисляется
    /// синхронно, а резолв идёт в том же await-контексте, что и обычное
    /// воспроизведение. Task.Run здесь нельзя: резолв читает БД через единственный
    /// SqliteConnection, параллельные запросы к нему не потокобезопасны.
    /// </summary>
    private void PrefetchNextPlatformTrack()
    {
        if (FilePathResolver == null) return;
        var next = GetNextPlatformCandidate(_queue, _queueIndex);
        if (next == null) return;
        if (Interlocked.Exchange(ref _prefetchInFlight, 1) == 1) return;
        _ = PrefetchResolveAsync(next);
    }

    /// <summary>Следующий трек очереди, требующий резолва файла (FilePath пуст, платформенный,
    /// доступен). null — префетчить нечего. Чистая функция.</summary>
    private static Track? GetNextPlatformCandidate(IReadOnlyList<Track>? queue, int currentIndex)
    {
        if (queue == null || currentIndex < 0) return null;
        var next = currentIndex + 1;
        if (next >= queue.Count) return null;
        var candidate = queue[next];
        if (!SoundCloudRuntimeTracks.NeedsFilePathResolve(candidate)) return null;
        if (!candidate.IsAvailable) return null;
        return candidate;
    }

    private async Task PrefetchResolveAsync(Track next)
    {
        try
        {
            await FilePathResolver!(next, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Prefetch не должен влиять на воспроизведение: ошибка только в лог.
            Logger.Error(ex, "SoundCloud next-track prefetch failed");
        }
        finally
        {
            Interlocked.Exchange(ref _prefetchInFlight, 0);
        }
    }

    // ==================== Засчитывание прослушки ====================

    /// <summary>Снять снимок «ожидаемой» прослушки (позицию движка читаем синхронно,
    /// пока трек ещё загружен — после Stop она невалидна) и очистить ожидание.</summary>
    private void SnapshotPendingListen()
    {
        if (_pendingListenTrack == null) return;
        _capturedListen = (_pendingListenTrack,
                           _engine.CurrentTime.Ticks - _pendingListenStartTicks,
                           _pendingListenReachedEnd);
        _pendingListenTrack = null;
        _pendingListenReachedEnd = false;
    }

    /// <summary>Засчитать снятую прослушку, если пройден порог (дослушан до конца
    /// либо прослушано достаточно). Ошибки записи — только в лог.</summary>
    private async Task CommitCapturedListenAsync()
    {
        if (_capturedListen is not { } captured) return;
        _capturedListen = null;

        try
        {
            if (!captured.ReachedEnd && captured.ListenedTicks < MinListenedTicks(captured.Track))
                return;
            await _history.RecordPlayAsync(captured.Track);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Play history record failed");
        }
    }

    /// <summary>Порог засчитывания прослушки в тиках: 15 c, для треков короче минуты — половина.</summary>
    private static long MinListenedTicks(Track track)
    {
        var duration = TimeSpan.FromTicks(track.DurationTicks);
        if (duration > TimeSpan.Zero && duration < ShortTrackCutoff)
            return (long)(duration.Ticks * ShortTrackFraction);
        return MinListened.Ticks;
    }

    /// <summary>
    /// Текст ошибки «трек недоступен» по источнику: у Яндекс Музыки недоступность —
    /// чаще всего тарифная (YmTrackUnavailable), у SC/VK — общий текст. Неизвестные
    /// источники получают историческое SC-сообщение.
    /// </summary>
    private static string UnavailableMessageKey(string source)
        => source == Track.SourceYandex ? "YmTrackUnavailable" : "SoundCloudUnavailable";

    /// <summary>
    /// Пропуск неиграбельного SC-трека при авто-переходе. Лимит подряд идущих
    /// пропусков (ResolveFailurePolicy.MaxConsecutiveUnresolvable) достигнут —
    /// Stop с тостом «SoundCloudUnavailable» вместо бесконечного цикла Next по
    /// мёртвой очереди. Успешный резолв сбрасывает счётчик.
    /// </summary>
    private void SkipUnresolvable()
    {
        _unresolvableStreak++;
        if (_unresolvableStreak < ResolveFailurePolicy.MaxConsecutiveUnresolvable)
        {
            AdvanceInQueue(ignoreRepeatOne: true);
            return;
        }

        _unresolvableStreak = 0;
        Logger.Warn($"Playback stopped after {ResolveFailurePolicy.MaxConsecutiveUnresolvable} consecutive unresolvable SoundCloud tracks");
        Stop();
        ErrorOccurred?.Invoke(this, Loc.Get(UnavailableMessageKey(CurrentTrack?.Source ?? Track.SourceSoundCloud)));
    }

    public void Next() => AdvanceInQueue(ignoreRepeatOne: false);

    /// <summary>
    /// Переход к следующей позиции очереди. ignoreRepeatOne — для пропуска неиграбельных
    /// SC-треков: RepeatOne повторял бы тот же недоступный трек вместо перехода дальше.
    /// </summary>
    private void AdvanceInQueue(bool ignoreRepeatOne)
    {
        if (_queue.Count == 0) return;

        if (_repeat == RepeatMode.RepeatOne && !ignoreRepeatOne)
        {
            // Цикл поворота завершён: снимок (reachedEnd уже выставлен) -> засчитали
            // -> перезаряжаем ожидание на тот же трек с нулевой позиции.
            SnapshotPendingListen();
            _ = CommitCapturedListenAsync();
            _pendingListenTrack = CurrentTrack;
            _pendingListenStartTicks = 0;
            _pendingListenReachedEnd = false;

            _engine.Seek(TimeSpan.Zero);
            _engine.Play();
            // Синхронизация VM: повтор с начала — таймлайн обязан показать 00:00.
            PositionChanged?.Invoke(this, TimeSpan.Zero);
            return;
        }

        int nextIdx;
        if (_shuffle)
        {
            // Guard: порядок может быть не готов (shuffle включён при пустой очереди
            // в RestoreStateAsync, очередь добивается позже — раньше здесь падал
            // NullReferenceException и ломался автопереход вместе со статистикой).
            if (_shuffleOrder == null || _shuffleOrder.Count == 0 || _shufflePos < 0)
                RebuildShuffleOrder();
            if (_shuffleOrder == null || _shuffleOrder.Count == 0)
            {
                _shuffle = false;
                return;
            }
            _shufflePos = (_shufflePos + 1) % _shuffleOrder.Count;
            nextIdx = _shuffleOrder[_shufflePos];
            if (nextIdx == _queueIndex && _repeat == RepeatMode.None && _shufflePos == 0)
            {
                Stop();
                QueueEnded?.Invoke(this, CurrentTrack);
                return;
            }
        }
        else
        {
            nextIdx = _queueIndex + 1;
            if (nextIdx >= _queue.Count)
            {
                if (_repeat == RepeatMode.RepeatAll) nextIdx = 0;
                else { Stop(); QueueEnded?.Invoke(this, CurrentTrack); return; }
            }
        }

        _queueIndex = nextIdx;
        PlayWithFade(false, userInitiated: false);
    }

    public void Previous()
    {
        if (_queue.Count == 0) return;
        if (_engine.CurrentTime > TimeSpan.FromSeconds(3))
        {
            _engine.Seek(TimeSpan.Zero);
            // Синхронизация VM: «начало трека» — таймлайн обязан показать 00:00.
            PositionChanged?.Invoke(this, TimeSpan.Zero);
            return;
        }

        int prevIdx;
        if (_shuffle)
        {
            _shufflePos = (_shufflePos - 1 + _shuffleOrder!.Count) % _shuffleOrder.Count;
            prevIdx = _shuffleOrder[_shufflePos];
        }
        else
        {
            prevIdx = _queueIndex - 1;
            if (prevIdx < 0) prevIdx = _repeat == RepeatMode.RepeatAll ? _queue.Count - 1 : 0;
        }

        _queueIndex = prevIdx;
        PlayWithFade(false, userInitiated: false);
    }

    public void Seek(TimeSpan position) => _engine.Seek(position);

    public void CycleRepeatMode()
    {
        RepeatMode = _repeat switch
        {
            RepeatMode.None      => RepeatMode.RepeatAll,
            RepeatMode.RepeatAll => RepeatMode.RepeatOne,
            _                    => RepeatMode.None
        };
    }

    /// <summary>
    /// Дозаписать незакоммиченную прослушку (выход из приложения): снимок играющего
    /// сейчас трека + коммит снятого. Идемпотентно — безопасно вызывать всегда
    /// (Stop/переходы уже могли всё закоммитить, тогда оба шага — no-op).
    /// </summary>
    public async Task FlushPendingListenAsync()
    {
        if (_pendingListenTrack != null)
        {
            SnapshotPendingListen();
        }
        await CommitCapturedListenAsync();
    }

    public async Task SaveStateAsync()
    {
        // Сначала дозаписываем прослушку играющего трека: иначе при выходе с играющим
        // треком (крестик/Exit из трея) последнее прослушивание терялось — Snapshot
        // некому было снять, а позиция движка после Shutdown уже невалидна.
        await FlushPendingListenAsync();

        if (CurrentTrack != null)
        {
            await _history.UpdatePlayStateAsync(CurrentTrack.Id, _engine.CurrentTime, _engine.IsPlaying);
        }

        var state = new Models.PlaybackState
        {
            CurrentTrackId = CurrentTrack?.Id,
            LastPositionTicks = _engine.CurrentTime.Ticks,
            Volume = (int)Math.Round(Volume),
            IsMuted = IsMuted,
            IsShuffle = IsShuffle,
            RepeatMode = RepeatMode,
            QueueTrackIds = _queue.Select(t => t.Id).ToList(),
            QueueIndex = _queueIndex,
            LastPlaylistId = null
        };
        await _library.SavePlaybackStateAsync(state);
    }

    public void ApplyEqualizerPreset(EqualizerPreset preset)
    {
        // Через state-хранилище (значения переживают смену трека).
        SetEqualizerPreGain(preset.PreGain);
        ApplyEqualizerBands(preset.Bands);
    }

    public void SetEqualizerEnabled(bool enabled) => _engine.SetEqualizerEnabled(enabled);

    /// <summary>Живое включение/выключение нормализации: выкл — усиление в 1 (рампой);
    /// вкл — усиление текущего трека пересчитывается по его измеренной громкости.</summary>
    public void SetNormalizationEnabled(bool enabled)
    {
        if (!enabled)
        {
            _engine.SetNormalizeGain(1f);
            return;
        }
        var file = CurrentTrack?.FilePath;
        if (!string.IsNullOrEmpty(file))
            _ = ApplyNormalizationAsync(file);
    }

    /// <summary>Заменить весь набор полос (пресет, массовая правка).</summary>
    public void ApplyEqualizerBands(IReadOnlyList<EqualizerBand> bands)
    {
        _eqBands.Clear();
        foreach (var b in bands)
            _eqBands.Add(CloneBand(b));
        _engine.ApplyEqualizerBands(_eqBands);
    }

    /// <summary>Обновить одну полосу (перетаскивание узла, смена типа/крутизны).</summary>
    public void UpdateEqualizerBand(int index, EqualizerBand band)
    {
        if (index < 0 || index >= _eqBands.Count) return;
        _eqBands[index] = CloneBand(band);
        _engine.UpdateEqualizerBand(index, band);
    }

    private static EqualizerBand CloneBand(EqualizerBand b)
        => new() { Index = b.Index, Frequency = b.Frequency, Gain = b.Gain, Type = b.Type, SlopeDbOct = b.SlopeDbOct, Q = b.Q, IsSolo = false };

    /// <summary>Соло «слушать гармонику»: null — выключить. Одна полоса за раз.</summary>
    public void SetEqualizerSolo(double? freqHz, double? q)
    {
        _soloFreq = freqHz;
        _soloQ = q;
        _engine.SetEqualizerSolo(freqHz, q);
    }

    public void SetEqualizerPreGain(double preGainDb)
    {
        _eqPreGain = preGainDb;
        _engine.SetEqualizerPreGain(preGainDb);
    }

    public IEnumerable<string> EnumerateOutputDevices()
        => AudioEngine.EnumerateDevices().Select(d => d.FriendlyName);

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // With event-sync WasapiOut this fires on the audio playback thread.
        // Next() -> engine.Open() -> Stop() joins that very thread, so running
        // it here deadlocks audio and the next UI play call. Marshal to UI.
        _dispatcher.BeginInvoke(() =>
        {
            if (e.Exception != null)
            {
                Logger.Error(e.Exception);
                Logger.Info("PlaybackStopped: error -> StopWithError path");
                ErrorOccurred?.Invoke(this, Loc.Get("ErrorPlaybackStopped"));
            }
            else
            {
                // Track finished naturally -> advance
                Logger.Info("PlaybackStopped: natural end -> Next()");
                _pendingListenReachedEnd = true;
                Next();
            }
        });
    }

    private void RebuildShuffleOrder()
    {
        if (!_shuffle || _queue.Count == 0) { _shuffleOrder = null; return; }
        var rng = new Random();
        var order = Enumerable.Range(0, _queue.Count).ToList();
        // Fisher-Yates
        for (int i = order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        _shuffleOrder = order;
        _shufflePos = order.IndexOf(_queueIndex);
    }

    public void Dispose()
    {
        _positionTimer?.Dispose();
        _engine.PlaybackStopped -= OnPlaybackStopped;
        _engine.Dispose();
    }
}
