using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

namespace BatPlayer.ViewModels;

/// <summary>
/// Card of a liked SoundCloud track. Built once at page load and after sync — so
/// computed properties (IsPlayable etc.) do not need INPC.
/// </summary>
public sealed class SoundCloudCard
{
    public required string ScId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Artist name for display (ArtistHoverTemplate binds exactly this): an empty
    /// value is replaced by the localized "Unknown artist".</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Cover path in the local cache (artworks_cache); null — not downloaded yet,
    /// the card shows an IconCloud placeholder. We bind the local path: i1.sndcdn.com is
    /// not directly reachable, and BitmapImage downloads without a proxy.</summary>
    public required string? ArtworkLocalPath { get; init; }

    public required string PermalinkUrl { get; init; }
    public required bool Streamable { get; init; }

    /// <summary>Matched track from the local library (null — no match).</summary>
    public Track? LocalTrack { get; init; }

    /// <summary>Whether there is a local-library match (IconFile badge, offline playback).</summary>
    public bool HasLocalMatch => LocalTrack != null;

    /// <summary>Playable: stream available OR a local match exists.</summary>
    public bool IsPlayable => Streamable || HasLocalMatch;

    /// <summary>Unavailable tracks are dimmed (modeled on unavailable local files).</summary>
    public double CardOpacity => IsPlayable ? 1.0 : 0.45;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// SoundCloud page: grid of liked tracks from the local DB (soundcloud_likes),
/// sync with /me/likes/tracks, matching with the local library, streaming.
/// Online tracks are downloaded to the disk cache (SoundCloudStreamCache) and played
/// from the local file.
/// </summary>
public partial class SoundCloudLikesViewModel : PageViewModel, ISearchablePage
{
    private readonly SoundCloudService _soundCloud;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SoundCloudLikesRepository _repository;

    // Local library for matching; re-read on every page load.
    private List<Track> _localTracks = new();

    // "Auto-sync on first open" — runs once per app lifetime.
    private bool _autoSyncChecked;

    // Cards already read from the DB: re-entering the page does not rebuild
    // the list (only the sync changes the data).
    private bool _cardsLoaded;

    /// <summary>Map "cover cache-file path → card" for the LazyCover loader: a visible
    /// card without a downloaded cover fetches it on demand (see ctor).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SoundCloudCard> _cardsByArtworkPath = new();

    public ObservableCollection<SoundCloudCard> Cards { get; } = new();

    // === Universal search (the bar in the window header) ===
    // The full card list is stored separately: Cards shows either everything or the
    // filtered subset; after a sync/reload the filter is applied again.
    private List<SoundCloudCard> _allCards = new();
    private string _searchQuery = string.Empty;

    /// <summary>Filters the page's cards by title and artist; an empty query shows the full list.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildCards();
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

    [ObservableProperty] private int _syncProgress;
    [ObservableProperty] private int _syncTotal;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _lastSyncedText = string.Empty;

    /// <summary>An SC track is being loaded into the cache: sync and card clicks are ignored.</summary>
    [ObservableProperty] private bool _isLoadingTrack;

    /// <summary>Cards are being read from the DB: skeletons are shown meanwhile.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Errors for the main window toast (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Whether an account is connected, by the presence of sc_auth.json (no network check).</summary>
    public bool IsConnected => _soundCloud.HasAuthFile;

    /// <summary>Show the "empty page": loading finished and there are no cards.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    /// <param name="streamCache">Unused: SC card files are resolved by the player
    /// (AudioService.FilePathResolver); the parameter is kept for call-site compatibility.</param>
    public SoundCloudLikesViewModel(SoundCloudService soundCloud, LibraryService library,
                                    AudioService audio, SoundCloudLikesRepository repository,
                                    SoundCloudStreamCache? streamCache = null)
    {
        _soundCloud = soundCloud;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = Loc.Get("SoundCloud");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("SoundCloud");
            RefreshHeaderText();
        };

        _soundCloud.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };

        // LazyCover loader: a realized (visible) card whose cover file is not yet
        // downloaded → fetch it now. The LazyCover queue is priority-based, so nearby
        // cards' covers download first; on scroll only visible ones. Batch sync
        // downloads the rest in the background via the same SoundCloudService dedup.
        Controls.LazyCover.SetFileLoader(async path =>
        {
            if (!_cardsByArtworkPath.TryGetValue(path, out var card)) return false;
            if (string.IsNullOrEmpty(card.ArtworkUrl)) return false;
            var downloaded = await _soundCloud
                .EnsureArtworkPathAsync(card.ScId, card.ArtworkUrl, CancellationToken.None)
                .ConfigureAwait(false);
            return downloaded != null;
        });
    }

    /// <summary>
    /// Called from MainViewModel.Navigate("SoundCloud"): re-read cards from the DB;
    /// on first open — auto-sync, if connected and &gt;30 minutes have passed.
    /// </summary>
    public async Task OnNavigatedAsync()
    {
        if (_cardsLoaded) return;

        await LoadCardsAsync();

        if (_autoSyncChecked) return;
        _autoSyncChecked = true;

        if (IsConnected && !IsSyncing)
        {
            var last = _soundCloud.GetLastSyncedUtc();
            if (last == null || DateTime.UtcNow - last.Value > TimeSpan.FromMinutes(30))
                await SyncNowAsync();
        }
    }

    private async Task LoadCardsAsync()
    {
        IsLoading = true;
        try
        {
            var localTask = _library.GetAllTracksAsync();
            var rowsTask = _repository.GetAllAsync();
            await Task.WhenAll(localTask, rowsTask);
            _localTracks = localTask.Result;
            var rows = rowsTask.Result;

            // The heavy part (match index + card building over the whole library) —
            // in the background: on the UI thread it held up layout and the page
            // transition lagged.
            var fresh = await Task.Run(() =>
            {
                // Matching via a prebuilt index: O(M) for the index + O(1) per card
                // (previously O(N*M) string comparisons on every page entry).
                var index = MatchHelper.BuildIndex(_localTracks);

                // Batched update: the panel implements only window virtualization, but
                // Clear+Add one by one gives N notifications; noticeable with hundreds
                // of cards. Build into a list.
                var list = new List<SoundCloudCard>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new SoundCloudCard
                    {
                        ScId = row.ScId,
                        Title = row.Title,
                        Artist = row.Artist,
                        DurationMs = row.DurationMs,
                        ArtworkUrl = row.ArtworkUrl,
                        ArtworkLocalPath = row.ArtworkLocalPath,
                        PermalinkUrl = row.PermalinkUrl,
                        Streamable = row.Streamable,
                        LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                    });
                }

                // Map for the LazyCover loader: from a cache path it finds the card and
                // downloads its cover on demand (the file may appear later via batch sync —
                // the loader then instantly returns the already-downloaded file).
                _cardsByArtworkPath.Clear();
                foreach (var card in list)
                    _cardsByArtworkPath[_soundCloud.GetArtworkCachePath(card.ScId)] = card;
                return list;
            });

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes load failed");
        }        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Shuffle the page's cards; if an SC track from this list is playing,
    /// the player queue is rebuilt in the new order.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.IsPlayable
                ? SoundCloudRuntimeTracks.BuildRuntimeTrack(c.ScId, c.Title, c.Artist,
                    c.DurationMs, c.ArtworkLocalPath, i)
                : null,
            (playing, c) => playing.Source == Track.SourceSoundCloud && playing.ScId == c.ScId,
            "SoundCloud");
    }

    private void RefreshHeaderText()
    {
        var last = _soundCloud.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("SoundCloudNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        CounterText = $"{Cards.Count} {Loc.Get("SoundCloudLikedCounter")}";
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing || IsLoadingTrack) return;
        if (!IsConnected)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("NotConnected"));
            return;
        }

        IsSyncing = true;
        SyncProgress = 0;
        SyncTotal = 0;
        try
        {
            await _soundCloud.SyncLikesAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
        }
        catch (SoundCloudApiException ex)
        {
            Logger.Error($"SoundCloud sync failed: HTTP {ex.StatusCode}");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("SoundCloudSyncFailed")}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudSyncFailed"));
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Card click: plays it (the file is resolved by the player via FilePathResolver —
    /// local match or mp3 from cache/network). Queue = all playable cards of the page
    /// (streamable or with a local match), so Previous/Next walk the whole list.
    /// Unavailable (not streamable, no match) — a toast explains.
    /// </summary>
    /// <summary>Anti-double-click: the artwork Play button's command and a MouseLeftButtonUp
    /// bubbling to the card fire the same command twice within milliseconds; the second
    /// call saw "track already playing" and paused it — the click "did not work first time".</summary>
    private SoundCloudCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(SoundCloudCard? card)
    {
        if (card == null || IsSyncing || IsLoadingTrack) return Task.CompletedTask;
        if (!card.IsPlayable)
        {
            // Not streamable and no local match — the click is empty by design; silence
            // looked like "the player is broken", so we explain with a toast.
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudUnavailable"));
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Clicking the playing SC card again — pause/resume. But while the track is
            // still OPENING (neither Playing nor Paused — stream resolution in progress),
            // toggling broke the open chain and sound did not appear on the first click.
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceSoundCloud && current.ScId == card.ScId)
            {
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Runtime cards are built on click (not stored on the page's cards):
            // FilePath is empty — resolved on every step through the queue.
            var queue = Cards.Where(c => c.IsPlayable)
                             .Select((c, i) => SoundCloudRuntimeTracks.BuildRuntimeTrack(
                                 c.ScId, c.Title, c.Artist, c.DurationMs, c.ArtworkLocalPath, i))
                             .ToList();
            var start = queue.First(t => t.ScId == card.ScId);
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
