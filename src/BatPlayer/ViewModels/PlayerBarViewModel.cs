using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Helpers;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.ViewModels;

/// <summary>
/// Bottom mini-player. Bound to AudioService and CurrentTrack.
/// </summary>
public partial class PlayerBarViewModel : ObservableObject
{
    private readonly AudioService _audio;
    private readonly LibraryService _library;
    private readonly CoverCacheService _covers;
    private readonly YmService _ym;

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    /// <summary>Id of the playlist whose queue is playing (null — queue is not a playlist):
    /// playlist cards use it to show Pause and keep the overlay.</summary>
    [ObservableProperty] private long? _currentPlaylistId;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    // double 0..100: TwoWay slider without rounding while dragging (thumb jitter gone)
    [ObservableProperty] private double _volume = 70;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isShuffle;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.None;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private BitmapImage? _coverImage;
    /// <summary>Seeking in progress: position ticks are muted until the engine catches up.</summary>
    [ObservableProperty] private bool _isSeeking;

    /// <summary>Seek guard (pure logic — see SeekSyncGuard).</summary>
    private readonly SeekSyncGuard _seekGuard = new();
    /// <summary>Fuse: resets the guard if PositionChanged never arrives (pause/error).</summary>
    private DispatcherTimer? _seekFuseTimer;

    public double ProgressPercent => Duration.TotalSeconds > 0
        ? Position.TotalSeconds / Duration.TotalSeconds * 100 : 0;

    public PlayerBarViewModel(AudioService audio, LibraryService library, CoverCacheService covers,
                              YmService ym)
    {
        _audio = audio;
        _library = library;
        _covers = covers;
        _ym = ym;

        _audio.CurrentTrackChanged += (_, t) => OnTrackChanged(t);
        _audio.PlayStateChanged      += (_, _) => IsPlaying = _audio.IsPlaying;
        _audio.QueueSourceChanged    += (_, id) => CurrentPlaylistId = id;
        // Position tick with the seek guard: while the engine has not caught up with the
        // seek target, stale positions are swallowed (otherwise the binding yanks the slider back).
        _audio.PositionChanged       += OnAudioPositionChanged;
        _audio.VolumeChanged         += (_, v) => Volume = v;
        _audio.MuteChanged           += (_, m) => IsMuted = m;
        _audio.ShuffleChanged        += (_, s) => IsShuffle = s;
        _audio.RepeatModeChanged     += (_, r) => RepeatMode = r;
    }

    private void OnTrackChanged(Track? t)
    {
        EndSeekGuard(); // track change zeroes positions — the seek guard is no longer needed
        CurrentTrack = t;
        Duration = t?.Duration ?? TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsFavorite = t?.IsFavorite ?? false;
        _ = LoadCoverAsync(t);
    }

    private void OnAudioPositionChanged(object? sender, TimeSpan p)
    {
        // Periodic resync: even if a PlayStateChanged notification is missed,
        // the icon can't stay out of sync with the actual engine state for long.
        IsPlaying = _audio.IsPlaying;

        if (_seekGuard.IsActive)
        {
            // Guard active: accept a tick only when the engine reached the seek target
            // (or the fuse expired) — see SeekSyncGuard.
            if (!_seekGuard.TryAccept(p, Environment.TickCount64)) return;
            _seekFuseTimer?.Stop();
            IsSeeking = false;

            // The first tick after accept must be at the target. If the engine reported
            // being far off (read race/decoder fault), do not move the slider:
            // the next tick (250 ms) will show the real position, avoiding a "snap-back".
            if (Duration > TimeSpan.Zero &&
                Math.Abs((p - _seekGuard.Target).TotalSeconds) > 1.0)
                return;
        }

        // Filter garbage ticks: position cannot be outside the track duration.
        // JUMPS are NOT filtered here: the engine seeks not only via SeekTo (resume
        // with saved position, RepeatOne, Previous) — after such seeks the VM must
        // accept the new value, otherwise the timeline freezes and any click "snaps back".
        if (Duration > TimeSpan.Zero &&
            (p < TimeSpan.Zero || p > Duration + TimeSpan.FromMilliseconds(500)))
            return;

        Position = p;
    }


    [RelayCommand] private void PlayPause() => _audio.PlayPauseToggle();
    [RelayCommand] private void Next()       => _audio.Next();
    [RelayCommand] private void Previous()   => _audio.Previous();
    [RelayCommand] private void ToggleShuffle() => _audio.IsShuffle = !_audio.IsShuffle;
    [RelayCommand] private void CycleRepeat()   => _audio.CycleRepeatMode();
    [RelayCommand] private void ToggleMute()    => _audio.IsMuted = !_audio.IsMuted;

    [RelayCommand]
    private void SeekTo(double percent)
    {
        if (Duration.TotalSeconds <= 0) return;
        var pos = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(percent, 0, 1));
        BatPlayer.Services.Logger.Info($"[SEEK] target={pos.TotalSeconds:0.00}");
        _audio.Seek(pos);
        // Optimistically show the target position and mute PositionChanged ticks
        // until the engine catches up: seek is asynchronous, and without this the
        // binding immediately snaps the timeline slider back to the old position.
        Position = pos;
        BeginSeekGuard(pos);
    }

    private void BeginSeekGuard(TimeSpan target)
    {
        _seekGuard.Begin(target, Environment.TickCount64);
        IsSeeking = true;
        if (_seekFuseTimer == null)
        {
            // Fuse for the case when PositionChanged never arrives
            // (pause without ticks, engine error): the guard must not hang forever.
            _seekFuseTimer = new DispatcherTimer(SeekSyncGuard.Fuse, DispatcherPriority.Background,
                (_, _) => EndSeekGuard(), Dispatcher.CurrentDispatcher);
        }
        _seekFuseTimer.Stop();
        _seekFuseTimer.Start(); // re-arm on every new seek
    }

    private void EndSeekGuard()
    {
        _seekFuseTimer?.Stop();
        _seekGuard.End();
        IsSeeking = false;
    }

    private async Task LoadCoverAsync(Track? t)
    {
        if (t == null)
        {
            CoverImage = null;
            return;
        }

        // Cover resolve order:
        // 1) CoverHash → CoverCacheService (cover from local file tags);
        // 2) fallback: CoverCachePath (ready file in cache) — the path of SC runtime
        //    cards (artwork_local_path): they have no CoverHash but the cover is downloaded.
        var path = string.IsNullOrEmpty(t.CoverHash)
            ? null
            : await _covers.GetOrCreateCoverAsync(t.FilePath, t.CoverHash);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(t.CoverCachePath)
            && File.Exists(t.CoverCachePath))
            path = t.CoverCachePath;

        // Intermediate CoverImage = null reset REMOVED: the old cover stays visible
        // until the new one is ready — track changes without dropping into the
        // placeholder, and the bar/Now Playing crossfade layers get a single value
        // change instead of two (null → image) that looked like a glitch.
        if (string.IsNullOrEmpty(path))
        {
            CoverImage = null;
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256; // player mini-cover: full-size decode not needed
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            // The track changed while decoding — do not show a foreign cover.
            if (!ReferenceEquals(CurrentTrack, t)) return;
            CoverImage = bmp;
        }
        catch { /* swallow */ }
    }

    public async Task RestoreAsync(bool openPlayback = true)
    {
        await _audio.RestoreStateAsync(openPlayback);
        Volume = _audio.Volume;
        IsMuted = _audio.IsMuted;
        IsShuffle = _audio.IsShuffle;
        RepeatMode = _audio.RepeatMode;
    }

    partial void OnVolumeChanged(double value) => _audio.Volume = value;

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (CurrentTrack == null) return;
        var track = CurrentTrack;

        // Liking a YM track goes to the Yandex Music ACCOUNT (POST/DELETE likes/tracks):
        // after a sync the track appears on the YM page and in Favorites. Previously the
        // heart silently wrote to the local DB by negative runtime id — no row existed
        // there and the like was lost ("like in a mix — never shows in favorites").
        if (track.Source == Track.SourceYandex)
        {
            var target = !track.IsFavorite;
            if (!await _ym.SetTrackLikedAsync(track.ScId, target))
                return; // API did not confirm — do not toggle the heart
            track.IsFavorite = target;
            IsFavorite = target;
            return;
        }

        // Other platform runtime cards (VK/SC/Spotify): no likes in this model.
        if (track.Id <= 0) return;

        IsFavorite = !IsFavorite;
        await _library.SetFavoriteAsync(track.Id, IsFavorite);
        track.IsFavorite = IsFavorite;
    }
}
