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
/// High-level audio service: queue, transitions, shuffle/repeat, state persistence.
/// Used by ViewModels; wraps AudioEngine internally.
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

    // ===== Queue context: the playlist the music is playing from =====
    // Set only by PlayTrack FROM a playlist (PlaylistViewModel): the playlist card
    // uses it to show Pause instead of Play and keep the overlay, like a playing
    // track card. Any other PlayTrack (library card, wave, search...) clears it —
    // the queue is no longer a playlist.
    private long? _playlistId;

    /// <summary>Id of the playlist whose queue is currently playing; null — the queue is not from a playlist.</summary>
    public long? CurrentPlaylistId => _playlistId;

    /// <summary>The queue source changed (playlist ↔ non-playlist).</summary>
    public event EventHandler<long?>? QueueSourceChanged;

    private void SetPlaylistContext(long? playlistId)
    {
        if (_playlistId == playlistId) return;
        _playlistId = playlistId;
        QueueSourceChanged?.Invoke(this, playlistId);
    }

    public TimeSpan Position => _engine.CurrentTime;
    public TimeSpan Duration => _engine.TotalTime;

    // User (target) volume 0..100. Stored SEPARATELY from the engine: fades write
    // 0 into the engine (fade-out on track change) but must not affect the target
    // volume — otherwise a new track starts silently until the slider is jiggled.
    private double _userVolume = 50;

    public double Volume // 0..100, double — no rounding while dragging the slider
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
    /// Resolves a local path for tracks without a file (SC runtime cards: FilePath
    /// empty, Source="soundcloud", ScId set). Called in PlayInternalAsync before Open;
    /// null means the file could not be obtained (auto-advance: the track is skipped;
    /// user click: Stop + error without skipping — ResolveFailurePolicy.Decide).
    /// Assigned in MainViewModel (SoundCloudService + on-disk stream cache).
    /// </summary>
    public Func<Track, CancellationToken, Task<string?>>? FilePathResolver { get; set; }

    public event EventHandler<Track?>? CurrentTrackChanged;
    public event EventHandler? PlayStateChanged;

    /// <summary>The queue played to the end (natural end of the last track, without
    /// RepeatAll/RepeatOne and without playback errors). The argument is the last
    /// played track. The "wave" uses this to auto-refresh the mix.</summary>
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

    // Fade durations at track boundaries: prevent clicks on switch/stop while
    // keeping transitions quick.
    private const int FadeTransitionMs = 300;
    private const int FadeOutClickMs = 140;
    private const int FadeInMs = 320;
    // Play/stop operation counter: stale async fade chains (rapid consecutive
    // switches, play right after a fade-stop) are cancelled through it.
    private int _opSeq;
    // Consecutive failed FilePathResolver resolves: guards against Next() looping
    // on unplayable SC tracks (geo/HLS-only). The failure policy (user click —
    // error without skipping; auto-advance — skip with a consecutive limit) is
    // ResolveFailurePolicy.Decide.
    private int _unresolvableStreak;
    // Re-entrancy guard for prefetch: at most one background resolve of the next
    // track at a time (rapid consecutive switches must not spawn parallel downloads).
    private int _prefetchInFlight;

    // ===== Listen counting (statistics) =====
    // A listen is recorded not on the Play click but AFTER listening: the track
    // played to the end OR >= 15 seconds actually listened (for short tracks
    // < 60s — half the duration). Skipping in the first seconds does not count.
    private static readonly TimeSpan MinListened = TimeSpan.FromSeconds(15);
    private const double ShortTrackFraction = 0.5;
    private static readonly TimeSpan ShortTrackCutoff = TimeSpan.FromSeconds(60);

    // Track with a "pending" listen (currently playing) and its start point.
    private Track? _pendingListenTrack;
    private long _pendingListenStartTicks;
    private bool _pendingListenReachedEnd;
    // Snapshot taken when leaving the track (engine position is invalid after Stop).
    private (Track Track, long ListenedTicks, bool ReachedEnd)? _capturedListen;

    // ===== Equalizer state =====
    // EqualizerSampleProvider is recreated on every Open: the bands (frequency,
    // gain, type, slope) and preamp are stored here and re-applied to the new
    // provider, otherwise the equalizer reset on every track change.
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
            // Position does not change while paused — ticks would just churn the UI
            // pipeline (BeginInvoke → subscribers → redraws). Skip: pause/start state
            // arrives via PlayStateChanged anyway.
            if (!_engine.IsPlaying) return;

            // Position is read ON THE UI THREAD, not the timer thread: otherwise a
            // tick queued before a click/seek delivers a STALE position AFTER the
            // seek — the seek guard lets it through (for a backward seek "p >= target"
            // holds for the old position too) and the slider rolls back short of the
            // click point. Reading here always sees the engine AFTER prior UI operations.
            _dispatcher.BeginInvoke(() => PositionChanged?.Invoke(this, _engine.CurrentTime));
        }, null, 100, 100);
    }

    /// <summary>Loads the saved state (call at startup after the library loads).
    /// openPlayback=false — window recreation: queue and UI are restored, but the
    /// playing track is NOT reopened (the audio service lives at app level and is already playing).</summary>
    public async Task RestoreStateAsync(bool openPlayback = true)
    {
        var state = await _library.LoadPlaybackStateAsync();
        if (state == null) return;

        if (!openPlayback)
        {
            // Window recreation: the service is already alive — volume/modes and the
            // queue must not be reloaded from the saved snapshot (it is stale, and
            // tracks had been ADDED to the live queue — the list doubled). The new
            // window only needs the current track and its play/pause state; the VM
            // takes the rest from the live service properties.
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
            // Guard: the queue may end up empty (the saved state had only SC/VK tracks
            // absent from the tracks table) — Clamp(0, -1) crashed startup.
            if (_queue.Count > 0)
                _queueIndex = Math.Clamp(state.QueueIndex, 0, _queue.Count - 1);
            // Shuffle order is built when IsShuffle is set, while the queue is still
            // EMPTY (restore starts with the settings) — rebuild it for the loaded
            // queue, otherwise _shuffleOrder stayed null and the first auto-advance
            // hit an NRE.
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
                    // VM sync: the engine was seeked to the saved position — the VM
                    // must learn it, otherwise the timeline showed 00:00 and any
                    // click on the bar "snapped back" to zero.
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

    /// <param name="playlistId">Playlist id if the queue is its tracks (playlist card
    /// click / double click on a details row). All other calls omit it — the playlist
    /// context is cleared.</param>
    public void PlayTrack(Track track, IEnumerable<Track>? contextQueue = null, long? playlistId = null)
    {
        // Remember what is actually audible: if the new track's resolve fails, the
        // "current" pointer returns to the audible track — otherwise the dead card
        // stays CurrentTrack and a repeated click toggles pause on the old one
        // (complaint: "pause gets set, the previous track turns on").
        var audibleTrack = CurrentTrack;
        SetPlaylistContext(playlistId);
        _queue.Clear();
        if (contextQueue != null)
            foreach (var t in contextQueue) _queue.Add(t);
        else
            _queue.Add(track);

        _queueIndex = _queue.IndexOf(track);
        if (_queueIndex < 0) { _queue.Add(track); _queueIndex = _queue.Count - 1; }

        // Explicit card click: a platform track (SoundCloud/VK) that failed a resolve
        // earlier this session (IsAvailable=false) is retried — network/VPN may be back.
        // Same object as in the VM list/queue, so the flag reset is visible to
        // auto-advance too.
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

    public void AddToQueue(Track track) => _queue.Add(track);

    public void Play()
    {
        // Play() is reachable only via PlayPauseToggle (button/hotkey/tray/re-click on
        // the current track card) — always user intent, not auto-advance: retry a dead
        // SC track instead of skipping it.
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
        // Leaving the track: snapshot the listened time before the engine stops,
        // then count it against the threshold.
        SnapshotPendingListen();
        _ = CommitCapturedListenAsync();

        // Smooth fade-stop: short fade-out, then Stop. The UI is not blocked — the
        // transition is async; PlayStateChanged fires after the actual stop so the
        // icon does not "flicker" back while the sound is still fading out.
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

    // === Volume normalization ===
    // Target level in RMS dBFS: modern streaming holds ~-14..-16 LUFS, roughly
    // -16 dBFS for an RMS proxy. Gain is clamped to ±9 dB: pull up very quiet
    // sources, tame very loud ones.
    private const double NormalizeTargetDb = -16.0;
    private const double NormalizeMaxGainDb = 9.0;

    /// <summary>Applies normalization to the open file (fire-and-forget): RMS loudness
    /// is computed alongside the waveform (WaveformCache, same decode) and arrives
    /// seconds after start — the gain ramps in via the provider, without a click.
    /// The sequence number (_opSeq) guards against applying to an already-closed track.</summary>
    private async Task ApplyNormalizationAsync(string filePath)
    {
        if (!_settings.Current.NormalizeVolume) return;
        int seq = _opSeq;
        try
        {
            var rmsDb = await WaveformCache.GetLoudnessDbAsync(filePath);
            if (seq != _opSeq) return; // track already changed — not our gain
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
    /// Fade-in of a new track to the target volume (fire-and-forget). The engine sets
    /// the target volume at the end of the fade; switching tracks cancels the fade
    /// via CancelFade before opening the next one — the new owner sets the volume.
    /// On failure the volume is restored manually while the operation is still current.
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
            // Sync the fade end with the current volume state: within 200ms the user
            // may have moved the slider or muted — the slider wins.
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

        if (seq != _opSeq) return; // a new operation (play/next) started — do not mute the new track
        _engine.CancelFade();
        _engine.Stop();
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Track switch: fade-out of the old track (if smooth transitions are on),
    /// then Open → volume → Play of the new one. Fade-in is intentionally omitted:
    /// the new track starts right at the target volume — the silent start ("no sound
    /// until you touch the volume") was caused by a fade chain interrupted between
    /// Play() and the volume restore.
    /// Everything runs on the UI context (await does not block the UI); on rapid
    /// switching a stale chain is cancelled via _opSeq.
    /// userInitiated — user click (card/Play) vs auto-advance (Next/track end):
    /// selects the failed-resolve policy (ResolveFailurePolicy).
    /// </summary>
    private async void PlayWithFade(bool resumeFromLast, bool userInitiated, Track? audibleTrack = null)
    {
        int seq = ++_opSeq;
        // Fade-out moved INSIDE PlayInternalAsync — AFTER the new track's resolve.
        // Volume used to fade before the network call, leaving silence for the
        // resolve duration (1-3s); a failed resolve left volume at zero — it looked
        // like "clicking the new track paused the current one".
        await PlayInternalAsync(resumeFromLast, seq, userInitiated, audibleTrack);
    }

    private async Task PlayInternalAsync(bool resumeFromLast, int seq, bool userInitiated, Track? audibleTrack = null)
    {
        if (CurrentTrack == null) return;

        // Leaving the previous track: snapshot its listen (engine still on the old
        // file) and count it if the threshold is met. Position is invalid after Open.
        SnapshotPendingListen();
        await CommitCapturedListenAsync();

        try
        {
            var s = _settings.Current;

            var filePath = CurrentTrack.FilePath;
            // SC runtime cards arrive with an empty FilePath: the file (local match or
            // mp3 from cache/network) is resolved here, within the same seq operation —
            // a track switch while waiting on the network cancels the stale chain via _opSeq.
            if (SoundCloudRuntimeTracks.NeedsFilePathResolve(CurrentTrack) && FilePathResolver != null)
            {
                // The track already failed a resolve this session (IsAvailable=false).
                // Auto-advance: do not hit the network again — skip immediately, the
                // transition stays instant. User click (marker reset in PlayTrack/Play)
                // — retry: network or VPN may be back.
                if (!CurrentTrack.IsAvailable && !userInitiated)
                {
                    Logger.Warn($"Track known unavailable (scId={CurrentTrack.ScId}) — skipping");
                    SkipUnresolvable();
                    return;
                }

                var resolved = await FilePathResolver(CurrentTrack, CancellationToken.None);
                if (seq != _opSeq) return; // switched to another track while resolving

                if (string.IsNullOrEmpty(resolved))
                {
                    // Track unavailable (not streamable / geo / network down): mark it on
                    // the runtime object so auto-advance does not retry the resolve this
                    // session. Then per ResolveFailurePolicy: user click → clean error
                    // without skipping; auto-advance → skip, consecutive count limited
                    // (otherwise Next() loops on a dead queue, RepeatAll).
                    CurrentTrack.IsAvailable = false;
                    Logger.Warn($"Track file resolve failed (scId={CurrentTrack.ScId}) — userInitiated={userInitiated}");
                    if (ResolveFailurePolicy.Decide(userInitiated, _unresolvableStreak) == ResolveFailureAction.StopWithError)
                    {
                        // Deterministic UX: click → it plays OR a clean error, no skipping.
                        // Open for the new track has not been called and the old one was
                        // not muted (fade now happens after the resolve) — it keeps playing;
                        // an error toast is enough.
                        _unresolvableStreak = 0;
                        // Restore "current" to the actually audible track: without this the
                        // dead track's card stays CurrentTrack (while the old one plays) — a
                        // repeated click looks like a click on the current track and pauses
                        // the old one; the next click resumes it.
                        var failedTrack = CurrentTrack;
                        if (userInitiated)
                        {
                            var audibleIdx = audibleTrack == null ? -1 : _queue.IndexOf(audibleTrack);
                            if (audibleIdx < 0 && audibleTrack != null)
                            {
                                // The audible track is not from this queue (click in another
                                // context): insert at the front so "current" points at it.
                                _queue.Insert(0, audibleTrack);
                                audibleIdx = 0;
                            }
                            _queueIndex = audibleIdx; // -1 — nothing was playing before the click
                            if (_shuffle) RebuildShuffleOrder();
                            CurrentTrackChanged?.Invoke(this, CurrentTrack);
                        }
                        PlayStateChanged?.Invoke(this, EventArgs.Empty);
                        // IMPORTANT: take the source from failedTrack (before the rollback);
                        // CurrentTrack may already be null here — this used to throw an NRE
                        // instead of showing the error.
                        ErrorOccurred?.Invoke(this, Loc.Get(UnavailableMessageKey(failedTrack.Source)));
                        return;
                    }
                    SkipUnresolvable();
                    return;
                }
                _unresolvableStreak = 0;
                filePath = resolved;
                // Success restores clickability: IsAvailable may still be false from a
                // previous failed attempt — clicking the card now plays the track again.
                CurrentTrack.IsAvailable = true;
                // Remember on the runtime object: a repeated card click pauses/resumes,
                // and transitions and "add to queue" see a ready file.
                CurrentTrack.FilePath = resolved;
            }

            // The new track is ready (file in hand): fade out the old one BEFORE Open.
            // Open swaps the output source and cuts the current sound instantly — a
            // concurrent fade-out would not be audible at all (a click sounded like a
            // hard cut). Transition formula: fade-out old → short Open → fade-in new.
            if (s.SmoothVolumeChanges && _engine.IsPlaying)
            {
                // User click — fast response (short fade-out); auto-advance (track
                // ended/Next) — longer and more musical.
                var fadeOutMs = userInitiated ? FadeOutClickMs : FadeTransitionMs;
                try
                {
                    await _engine.FadeVolumeAsync(0f, fadeOutMs);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                if (seq != _opSeq) return; // switched during the fade
            }

            _engine.CancelFade();
            _engine.Open(filePath, s.UseWasapiExclusive, s.AudioOutputDevice);
            _engine.SetEqualizerEnabled(s.EqualizerEnabled);
            // EQ was recreated at Open: re-apply the stored bands and preamp.
            _engine.SetEqualizerPreGain(_eqPreGain);
            _engine.ApplyEqualizerBands(_eqBands);
            _engine.SetEqualizerSolo(_soloFreq, _soloQ);
            // Volume normalization: the per-file gain is computed in the background
            // (RMS from the same decode as the waveform) and ramps in, without a click.
            _ = ApplyNormalizationAsync(filePath);

            if (resumeFromLast && CurrentTrack.LastPositionTicks > 0)
                _engine.Seek(CurrentTrack.LastPosition);

            // Strict order: Open → volume → Play. With smooth transitions the new track
            // starts at zero and rises via fade-in (previously only the old track faded
            // out — switching sounded like a hard cut); without smoothness the volume is
            // applied BEFORE start — the track cannot begin silently.
            _engine.CancelFade(); // the old track's fade must not write volume into the new provider
            var targetVolume = (IsMuted || _userVolume <= 0) ? 0f : (float)(_userVolume / 100.0);
            var fadeIn = s.SmoothVolumeChanges && targetVolume > 0f;
            _engine.Volume = fadeIn ? 0f : targetVolume;
            _engine.Play();
            if (fadeIn)
                _ = FadeInNewTrackAsync(targetVolume, seq);
            else
                _engine.Volume = targetVolume; // second guarantee: re-apply the volume AFTER Play

            Logger.Info($"PlayInternal: target={targetVolume:0.00} engine={_engine.Volume:0.00} src={CurrentTrack.Source} file={(SoundCloudRuntimeTracks.NeedsFilePathResolve(CurrentTrack) ? "resolved" : CurrentTrack.FilePath)}");

            // VM sync: the track may have started at its saved position (resumeFromLast)
            // — the timeline must show it immediately.
            PositionChanged?.Invoke(this, _engine.CurrentTime);

            CurrentTrackChanged?.Invoke(this, CurrentTrack);
            PlayStateChanged?.Invoke(this, EventArgs.Empty);

            // Pending listen: counted after sufficient listening (see
            // SnapshotPendingListen/CommitCapturedListenAsync) — on switching to the
            // next track, natural end, or Stop.
            _pendingListenTrack = CurrentTrack;
            _pendingListenStartTicks = _engine.CurrentTime.Ticks;
            _pendingListenReachedEnd = false;

            // Prefetch the queue's next platform track (SC/VK/Yandex Music): the mp3
            // downloads into the platform cache in the background, so a manual switch
            // starts without a download lag.
            PrefetchNextPlatformTrack();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            // Do not leave volume at zero after a failed switch: the old track may
            // still be loaded in the engine — restore its volume.
            if (_engine.IsPlaying)
            {
                _engine.CancelFade();
                _engine.Volume = (IsMuted || _userVolume <= 0) ? 0f : (float)(_userVolume / 100.0);
            }
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
        }
    }

    /// <summary>
    /// Background prefetch of the queue's next platform track (SC/VK/Yandex Music)
    /// after a successful track start: by the time the user switches, the file is
    /// already cached — no lag. The candidate comes from the pure function
    /// GetNextPlatformCandidate (strictly current+1). The shared FilePathResolver
    /// (the same one that plays tracks) fetches the file — the result path is
    /// irrelevant, the point is filling the platform cache.
    /// Threading: called from PlayInternalAsync (UI context) — the candidate is
    /// computed synchronously and the resolve runs in the same await context as
    /// normal playback. Task.Run is not allowed here: the resolve reads the DB via
    /// a single SqliteConnection, which is not safe for parallel queries.
    /// </summary>
    private void PrefetchNextPlatformTrack()
    {
        if (FilePathResolver == null) return;
        var next = GetNextPlatformCandidate(_queue, _queueIndex);
        if (next == null) return;
        if (Interlocked.Exchange(ref _prefetchInFlight, 1) == 1) return;
        _ = PrefetchResolveAsync(next);
    }

    /// <summary>The next queue track needing a file resolve (FilePath empty, platform,
    /// available). null — nothing to prefetch. Pure function.</summary>
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
            // Prefetch must not affect playback: the error goes to the log only.
            Logger.Error(ex, "SoundCloud next-track prefetch failed");
        }
        finally
        {
            Interlocked.Exchange(ref _prefetchInFlight, 0);
        }
    }

    // ==================== Listen counting ====================

    /// <summary>Snapshots the "pending" listen (the engine position is read synchronously
    /// while the track is still loaded — it is invalid after Stop) and clears the pending state.</summary>
    private void SnapshotPendingListen()
    {
        if (_pendingListenTrack == null) return;
        _capturedListen = (_pendingListenTrack,
                           _engine.CurrentTime.Ticks - _pendingListenStartTicks,
                           _pendingListenReachedEnd);
        _pendingListenTrack = null;
        _pendingListenReachedEnd = false;
    }

    /// <summary>Counts the captured listen if the threshold is met (played to the end
    /// or listened long enough). Write errors go to the log only.</summary>
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

    /// <summary>Listen-counting threshold in ticks: 15s, or half the duration for tracks under a minute.</summary>
    private static long MinListenedTicks(Track track)
    {
        var duration = TimeSpan.FromTicks(track.DurationTicks);
        if (duration > TimeSpan.Zero && duration < ShortTrackCutoff)
            return (long)(duration.Ticks * ShortTrackFraction);
        return MinListened.Ticks;
    }

    /// <summary>
    /// "Track unavailable" message key by source: for Yandex Music unavailability is
    /// usually a subscription issue (YmTrackUnavailable), for SC/VK a generic text.
    /// Unknown sources get the historical SC message.
    /// </summary>
    private static string UnavailableMessageKey(string source)
        => source == Track.SourceYandex ? "YmTrackUnavailable" : "SoundCloudUnavailable";

    /// <summary>
    /// Skips an unplayable SC track on auto-advance. When the consecutive-skip limit
    /// (ResolveFailurePolicy.MaxConsecutiveUnresolvable) is reached — Stop with a
    /// "SoundCloudUnavailable" toast instead of an endless Next loop over a dead
    /// queue. A successful resolve resets the counter.
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
    /// Advances to the next queue position. ignoreRepeatOne — for skipping unplayable
    /// SC tracks: RepeatOne would repeat the same unavailable track instead of moving on.
    /// </summary>
    private void AdvanceInQueue(bool ignoreRepeatOne)
    {
        if (_queue.Count == 0) return;

        if (_repeat == RepeatMode.RepeatOne && !ignoreRepeatOne)
        {
            // Repeat cycle completed: snapshot (reachedEnd already set) → counted →
            // re-arm the pending listen on the same track from position zero.
            SnapshotPendingListen();
            _ = CommitCapturedListenAsync();
            _pendingListenTrack = CurrentTrack;
            _pendingListenStartTicks = 0;
            _pendingListenReachedEnd = false;

            _engine.Seek(TimeSpan.Zero);
            _engine.Play();
            // VM sync: repeat from the start — the timeline must show 00:00.
            PositionChanged?.Invoke(this, TimeSpan.Zero);
            return;
        }

        int nextIdx;
        if (_shuffle)
        {
            // Guard: the order may not be ready (shuffle was enabled on an empty queue
            // in RestoreStateAsync and the queue is filled later — this used to throw
            // an NRE, breaking auto-advance along with the statistics).
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
            // VM sync: "start of track" — the timeline must show 00:00.
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
    /// Flushes uncommitted listens (app exit): snapshot the currently playing track
    /// and commit the captured one. Idempotent — safe to call anytime (Stop/switches
    /// may have committed everything already; both steps are no-ops then).
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
        // Flush the playing track's listen first: otherwise exiting while playing
        // (close button/Exit from tray) lost the last listen — no one took the
        // snapshot and the engine position is invalid after Shutdown.
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
        // Via the state store (values survive track changes).
        SetEqualizerPreGain(preset.PreGain);
        ApplyEqualizerBands(preset.Bands);
    }

    public void SetEqualizerEnabled(bool enabled) => _engine.SetEqualizerEnabled(enabled);

    /// <summary>Toggles normalization live: off — gain ramps to 1; on — the current
    /// track's gain is recomputed from its measured loudness.</summary>
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

    /// <summary>Replaces the whole set of bands (preset, bulk edit).</summary>
    public void ApplyEqualizerBands(IReadOnlyList<EqualizerBand> bands)
    {
        _eqBands.Clear();
        foreach (var b in bands)
            _eqBands.Add(CloneBand(b));
        _engine.ApplyEqualizerBands(_eqBands);
    }

    /// <summary>Updates a single band (node drag, type/slope change).</summary>
    public void UpdateEqualizerBand(int index, EqualizerBand band)
    {
        if (index < 0 || index >= _eqBands.Count) return;
        _eqBands[index] = CloneBand(band);
        _engine.UpdateEqualizerBand(index, band);
    }

    private static EqualizerBand CloneBand(EqualizerBand b)
        => new() { Index = b.Index, Frequency = b.Frequency, Gain = b.Gain, Type = b.Type, SlopeDbOct = b.SlopeDbOct, Q = b.Q, IsSolo = false };

    /// <summary>Solo "listen to harmonic": null — off. One band at a time.</summary>
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
