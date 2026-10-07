using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.ViewModels;

/// <summary>
/// Wave track card (any source: a Yandex candidate or a "revived" track from the
/// user's own VK/SC/local libraries). ArtworkLocalPath and IsCurrent raise
/// PropertyChanged: covers are downloaded in the background after generation, and the
/// "now playing" flag updates on every player track change.
/// </summary>
public sealed class WaveCard : System.ComponentModel.INotifyPropertyChanged
{
    public required string Source { get; init; }
    /// <summary>Platform identifier (ym_id / vk_id / sc_id / local:{id}); for local
    /// it equals the runtime track's Track.ScId.</summary>
    public required string PlatformId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    public bool Streamable { get; init; } = true;

    /// <summary>Matched track from the local library (for local — the track itself): the
    /// "available locally" badge on the card.</summary>
    public Track? LocalTrack { get; init; }

    public bool HasLocalMatch => LocalTrack != null;
    public bool IsPlayable => Streamable || HasLocalMatch;
    public double CardOpacity => IsPlayable ? 1.0 : 0.45;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Loc.Get("UnknownArtist")
        : Artist;

    private string? _artworkLocalPath;

    /// <summary>Cover path in the local cache; null — placeholder.</summary>
    public string? ArtworkLocalPath
    {
        get => _artworkLocalPath;
        set
        {
            if (_artworkLocalPath == value) return;
            _artworkLocalPath = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ArtworkLocalPath)));
        }
    }

    private bool _isCurrent;

    /// <summary>This track is currently in the player (any source, matched by Source+PlatformId).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// "My Wave" page: taste-based recommendations from the user's own music. On the left —
/// the "vinyl": the wave's current track (spins while playing), with the previous one
/// to its left and the next one below — both dimmed. On the right — a grid of the whole
/// wave: Yandex candidates and tracks from the user's libraries (VK/SC/local), each with
/// its own source logo.
///
/// Playback uses runtime tracks of the corresponding source: the player resolves
/// local match → cache → platform stream. Clicking any card
/// (grid or hero) plays the wave as a queue: Next/Previous walk the recommendations.
/// </summary>
public partial class WaveViewModel : PageViewModel, ISearchablePage
{
    private readonly RecommendationService _wave;
    private readonly YmService _ym;
    private readonly SoundCloudService _soundCloud;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly YmTracksRepository _ymTracks;

    /// <summary>Minimum interval between YM like syncs during automatic
    /// regenerations (radio after queue end): do not hit the API on every track.</summary>
    private static readonly TimeSpan AutoResyncInterval = TimeSpan.FromMinutes(5);

    private List<Track> _localTracks = new();

    public ObservableCollection<WaveCard> Cards { get; } = new();

    // === Universal search (the bar in the window header) ===
    // The last generated wave is stored in full: Cards shows either everything or the
    // filtered subset. The hero zone and the player queue are built from Cards.
    private List<WaveCard> _allCards = new();
    private string _searchQuery = string.Empty;

    /// <summary>Filters the wave's cards by title and artist; an empty query shows the full list.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildCards();
        UpdateCurrentFlags();
    }

    private void RebuildCards()
    {
        var q = _searchQuery.Trim();
        Cards.Clear();
        foreach (var card in _allCards)
            if (q.Length == 0
                || card.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || card.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                Cards.Add(card);
        RefreshHeaderText();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _counterText = string.Empty;

    // ===== Hero "vinyl" zone (left half of the page) =====
    [ObservableProperty] private WaveCard? _heroCurrent;
    [ObservableProperty] private WaveCard? _heroPrev;
    [ObservableProperty] private WaveCard? _heroNext;

    /// <summary>The hero track is actually playing (not merely selected): spins the vinyl.</summary>
    [ObservableProperty] private bool _isHeroPlaying;

    /// <summary>Errors for the main window toast (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Whether Yandex Music is connected — without it the similarity graph is unavailable.</summary>
    public bool IsConnected => _ym.HasToken;

    public bool ShowEmptyState => !IsLoading && !IsGenerating && Cards.Count == 0;

    public WaveViewModel(RecommendationService wave, YmService ym, SoundCloudService soundCloud,
                         LibraryService library, AudioService audio, YmTracksRepository ymTracks)
    {
        _wave = wave;
        _ym = ym;
        _soundCloud = soundCloud;
        _library = library;
        _audio = audio;
        _ymTracks = ymTracks;
        Title = Loc.Get("WaveTitle");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("WaveTitle");
            RefreshHeaderText();
        };

        // The player advances the wave itself at track end (queue = wave): the hero and
        // the current card highlight must follow CurrentTrack; play/pause spins the vinyl.
        _audio.CurrentTrackChanged += (_, _) => OnPlaybackChanged();
        _audio.PlayStateChanged += (_, _) => OnPlaybackChanged();
        // The wave played to the end — the mix refreshes itself and keeps playing (radio).
        _audio.QueueEnded += OnWaveQueueEnded;
    }

    /// <summary>Syncs hero/highlight with the player (events may arrive off the UI
    /// thread — move onto the dispatcher).</summary>
    private void OnPlaybackChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) UpdateCurrentFlags();
        else dispatcher.BeginInvoke(UpdateCurrentFlags);
    }

    /// <summary>Called from MainViewModel.Navigate("Wave") after the transition animation:
    /// the first generation of a session runs immediately, re-entries show the same
    /// list — refresh is via the button (the wave must be predictable within a session).</summary>
    public async Task OnNavigatedAsync()
    {
        if (Cards.Count > 0 || IsGenerating) return;
        await RunRefreshAsync(forceSync: true); // first mix of the session — from fresh likes
    }

    [RelayCommand]
    private Task RefreshAsync() => RunRefreshAsync(forceSync: true);

    /// <summary>Generation core: true — the wave was replaced successfully (for auto-refresh,
    /// which decides whether to start a new mix).</summary>
    private async Task<bool> RunRefreshAsync(bool forceSync = false)
    {
        if (IsGenerating) return false;
        if (!IsConnected)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("WaveNotConnected"));
            return false;
        }

        IsGenerating = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        Logger.Info("Wave: generation started");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await SyncYmLikesAsync(forceSync, cts.Token);
            _localTracks = await _library.GetAllTracksAsync();
            // Seed sources — on the caller's thread (shared connection); generation — background.
            var sources = await _wave.GatherSourcesAsync(cts.Token);
            var items = await _wave.GenerateWaveAsync(sources, cts.Token);
            Logger.Info($"Wave: generated {items.Count} candidates");

            var cards = await Task.Run(() => BuildCards(items), cts.Token);
            _allCards = cards;
            RebuildCards();
            UpdateCurrentFlags(); // the hero appears right away; covers catch up in the background

            await DownloadArtworksAsync(cards, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Wave: generation timed out or cancelled");
            ErrorOccurred?.Invoke(this, Loc.Get("WaveFailed"));
        }
        catch (YmApiException ex)
        {
            Logger.Error($"Wave generation failed (HTTP {ex.HttpCode})");
            ErrorOccurred?.Invoke(this, YmApiException.IsSessionError(ex.HttpCode)
                ? Loc.Get("YmSessionExpired")
                : Loc.Get("WaveFailed"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave generation failed");
            ErrorOccurred?.Invoke(this, Loc.Get("WaveFailed"));
        }
        finally
        {
            IsGenerating = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
        return false;
    }

    /// <summary>Syncs Yandex Music likes before generation: the mix is built from the
    /// local DB (ym_tracks), and without a sync "Refresh mix" would not see tracks added
    /// to YM after the last sync. Manual refresh and the first mix of a session always
    /// sync; auto radio restart — no more often than AutoResyncInterval. A sync failure
    /// (network/token) does not kill generation: the mix builds from the current DB, and
    /// a session error will surface from the recommendation requests themselves.</summary>
    private async Task SyncYmLikesAsync(bool force, CancellationToken ct)
    {
        if (!IsConnected) return;
        var last = _ym.GetLastSyncedUtc();
        if (!force && last != null && DateTime.UtcNow - last.Value < AutoResyncInterval) return;

        try
        {
            Logger.Info("Wave: syncing Yandex Music likes before generation");
            await _ym.SyncLikedTracksAsync(_ymTracks, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave: YM likes sync failed — generating from current DB");
        }
    }

    /// <summary>
    /// The wave played to the end (player queue exhausted): the mix refreshes itself
    /// and the new mix plays right away — radio with no user action. An exhausted
    /// queue that is not the wave — ignore; the event may come from the audio thread —
    /// continue on the UI thread.
    /// </summary>
    private async void OnWaveQueueEnded(object? sender, Track? lastTrack)
    {
        try
        {
            if (lastTrack == null) return;
            if (!Cards.Any(c => c.Source == lastTrack.Source && c.PlatformId == lastTrack.ScId))
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (!dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(() => OnWaveQueueEnded(sender, lastTrack));
                return;
            }

            if (!await RunRefreshAsync()) return; // a generation error is already shown
            if (Cards.Count > 0) await PlayCardAsync(Cards[0]);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave auto-refresh failed");
        }
    }

    private List<WaveCard> BuildCards(List<WaveItem> items)
    {
        var index = MatchHelper.BuildIndex(_localTracks);
        return items.Select(item =>
        {
            var localTrack = item.Source == Track.SourceLocal
                ? _localTracks.FirstOrDefault(t => item.PlatformId == $"local:{t.Id}")
                : MatchHelper.FindLocalMatch(index, item.Artist, item.Title);

            return new WaveCard
            {
                Source = item.Source,
                PlatformId = item.PlatformId,
                Title = item.Title,
                Artist = item.Artist,
                DurationMs = item.DurationMs,
                ArtworkUrl = YmJsonParser.BuildArtworkUrl(item.CoverUri) ?? string.Empty,
                Streamable = item.Available,
                LocalTrack = localTrack,
                ArtworkLocalPath = item.LocalPath // VK/SC: cover already in cache
            };
        }).ToList();
    }

    /// <summary>Batch download of candidate covers into the shared artworks_cache (up to 4
    /// parallel downloads): Yandex — via YmArtworkCache, SoundCloud — via the SC proxy
    /// layer (i1.sndcdn.com is not directly reachable). VK/local arrive with ready paths.</summary>
    private async Task DownloadArtworksAsync(List<WaveCard> cards, CancellationToken ct)
    {
        var pending = cards
            .Where(c => c.ArtworkLocalPath == null && c.ArtworkUrl.Length > 0
                        && (c.Source == Track.SourceYandex || c.Source == Track.SourceSoundCloud))
            .ToList();
        if (pending.Count == 0) return;

        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(pending.Select(async card =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = card.Source == Track.SourceYandex
                    ? await _ym.EnsureArtworkAsync(card.PlatformId, card.ArtworkUrl, ct)
                    : await _soundCloud.EnsureArtworkPathAsync(card.PlatformId, card.ArtworkUrl, ct);
                return (card, path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave artwork download failed ({card.Source}:{card.PlatformId})");
                return (card, null);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (card, path) in results)
        {
            if (path == null) continue;
            card.ArtworkLocalPath = path; // card INPC → LazyCover.Path re-reads
        }
    }

    private void RefreshHeaderText()
        => CounterText = $"{Cards.Count} {Loc.Get("WaveCounter")}";

    // ===================== Hero "vinyl" =====================

    /// <summary>Recomputes the hero trio and the current-card highlight from player state.
    /// Nothing playing — the hero shows the wave's first card (not spinning).</summary>
    private void UpdateCurrentFlags()
    {
        var current = _audio.CurrentTrack;
        foreach (var card in Cards)
            card.IsCurrent = current != null
                             && current.Source == card.Source
                             && current.ScId == card.PlatformId;

        var idx = current != null
            ? Cards.ToList().FindIndex(c => c.IsCurrent)
            : -1;

        if (idx < 0)
        {
            // No wave track playing: hero = first card, vinyl not spinning.
            // Side slots are set BEFORE HeroCurrent: in the PropertyChanged handler
            // (fly-over animation in the View) both slots must already be current.
            HeroPrev = null;
            HeroNext = Cards.Count > 1 ? Cards[1] : null;
            HeroCurrent = Cards.Count > 0 ? Cards[0] : null;
            IsHeroPlaying = false;
            return;
        }

        HeroPrev = idx > 0 ? Cards[idx - 1] : null;
        HeroNext = idx + 1 < Cards.Count ? Cards[idx + 1] : null;
        HeroCurrent = Cards[idx];
        IsHeroPlaying = _audio.IsPlaying;
    }

    // ===================== Playback =====================

    /// <summary>Runtime track of a card by its source: Yandex — like the YM page cards,
    /// VK/SC — like their pages' cards, local — a library file (plays offline, no
    /// resolve). Platform cards have an empty FilePath — resolved by the player on transitions.</summary>
    private static Track BuildRuntimeTrack(WaveCard card, int index)
    {
        if (card.Source == Track.SourceYandex)
            return YmRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, card.Streamable, index);
        if (card.Source == Track.SourceVk)
            return VkRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, index);
        if (card.Source == Track.SourceSoundCloud)
            return SoundCloudRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, index);

        // local: the file is known — plays directly, no resolve needed.
        return new Track
        {
            Id = -1 - index,
            FilePath = card.LocalTrack?.FilePath ?? string.Empty,
            ScId = card.PlatformId,
            Title = card.Title,
            Artist = card.Artist,
            DurationTicks = card.DurationMs * TimeSpan.TicksPerMillisecond,
            IsFavorite = card.LocalTrack?.IsFavorite ?? false,
            IsAvailable = true,
            Source = Track.SourceLocal
        };
    }

    /// <summary>
    /// Card click (hero, prev/next, grid): plays the wave as a queue.
    /// Clicking the playing one again — pause/resume. Anti-double-click follows
    /// the YmMusicViewModel pattern (Play button and MouseLeftButtonUp within milliseconds).
    /// </summary>
    private WaveCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(WaveCard? card)
    {
        if (card == null || IsGenerating) return Task.CompletedTask;
        if (!card.IsPlayable) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Clicking the playing wave card again — pause/resume.
            if (_audio.CurrentTrack is Track current
                && current.Source == card.Source && current.ScId == card.PlatformId)
            {
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            var queue = Cards.Select((c, i) => BuildRuntimeTrack(c, i)).ToList();
            var start = queue[Cards.IndexOf(card)];
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
