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
using BatPlayer.Services.Vk;

namespace BatPlayer.ViewModels;

/// <summary>
/// VK Music track card. Built once at page load and after sync — so computed
/// properties (IsPlayable etc.) do not need INPC.
/// </summary>
public sealed class VkCard
{
    public required string VkId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Artist name for display (ArtistHoverTemplate binds exactly this): an empty
    /// value is replaced by the localized "Unknown artist".</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Cover path in the local cache (artworks_cache/vk_{vk_id}.jpg); null — not
    /// downloaded yet, the card shows an IconVk placeholder.</summary>
    public required string? ArtworkLocalPath { get; init; }

    /// <summary>Whether the track is streamable. VK has no streamable flag like SoundCloud:
    /// everything from the al_audio catalog is playable (the link resolves on click).</summary>
    public bool Streamable { get; init; } = true;

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
/// VK Music page: grid of tracks from the local DB (vk_tracks), sync via the
/// al_audio.php web endpoint (web-session cookies), matching with the local library,
/// streaming. Online tracks are downloaded to the disk cache (VkStreamCache) and played
/// from the local file; user-facing file downloads are not provided — the cache is for playback only.
/// </summary>
public partial class VkMusicViewModel : PageViewModel, ISearchablePage
{
    private readonly VkService _vk;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly VkTracksRepository _repository;

    // Local library for matching; re-read on every page load.
    private List<Track> _localTracks = new();

    // "Auto-sync on first open" — runs once per app lifetime.
    private bool _autoSyncChecked;

    // Cards already read from the DB: re-entering the page
    // does not rebuild the list (only the sync changes the data).
    private bool _cardsLoaded;

    public ObservableCollection<VkCard> Cards { get; } = new();

    // === Universal search (the bar in the window header) ===
    // The full card list is stored separately: Cards shows either everything or the
    // filtered subset; after a sync/reload the filter is applied again.
    private List<VkCard> _allCards = new();
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

    /// <summary>A VK track is being loaded into the cache: sync and card clicks are ignored.</summary>
    [ObservableProperty] private bool _isLoadingTrack;

    /// <summary>Cards are being read from the DB: skeletons are shown meanwhile.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Errors for the main window toast (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Whether an account is connected, by the presence of web-session cookies (no network check).</summary>
    public bool IsConnected => _vk.HasWebSession;

    /// <summary>Show the "empty page": loading finished and there are no cards.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    public VkMusicViewModel(VkService vk, LibraryService library, AudioService audio,
                            VkTracksRepository repository)
    {
        _vk = vk;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = Loc.Get("VkMusic");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("VkMusic");
            RefreshHeaderText();
        };

        _vk.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };
    }

    /// <summary>
    /// Called from MainViewModel.Navigate("VkMusic"): re-read cards from the DB;
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
            var last = _vk.GetLastSyncedUtc();
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

            // Matching via a prebuilt index: O(M) for the index + O(1) per card.
            // The heavy part (match index + card building over the whole library) —
            // in the background: on the UI thread it held up layout and the page
            // transition lagged.
            var fresh = await Task.Run(() =>
            {
                var index = MatchHelper.BuildIndex(_localTracks);

                var list = new List<VkCard>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new VkCard
                {
                    VkId = row.VkId,
                    Title = row.Title,
                    Artist = row.Artist,
                    DurationMs = row.DurationMs,
                    ArtworkUrl = row.ArtworkUrl,
                    ArtworkLocalPath = row.ArtworkLocalPath,
                    LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                });
            }

                return list;
            });

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK music load failed");
        }        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Shuffle the page's cards; if a VK track from this list is playing,
    /// the player queue is rebuilt in the new order.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing || IsLoadingTrack) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.IsPlayable
                ? VkRuntimeTracks.BuildRuntimeTrack(c.VkId, c.Title, c.Artist, c.DurationMs,
                    c.ArtworkLocalPath, i)
                : null,
            (playing, c) => playing.Source == Track.SourceVk && playing.ScId == c.VkId,
            "VK Music");
    }

    private void RefreshHeaderText()
    {
        var last = _vk.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("VkNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        CounterText = $"{Cards.Count} {Loc.Get("VkCounter")}";
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
            await _vk.SyncAudioAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
        }
        catch (VkApiException ex)
        {
            Logger.Error($"VK sync failed (error code {ex.ErrorCode})");
            ErrorOccurred?.Invoke(this, DescribeSyncError(ex));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("VkSyncFailed"));
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Friendly message by VK error code: 5 — cookies expired (session reset by the
    /// service), 15/26/201 — no audio access, otherwise — generic sync text.
    /// </summary>
    private static string DescribeSyncError(VkApiException ex)
    {
        if (ex.ErrorCode == 5) return Loc.Get("VkSessionExpired");
        if (VkService.IsAudioPermissionError(ex.ErrorCode)) return Loc.Get("VkAudioPermissionDenied");
        return $"{Loc.Get("VkSyncFailed")}: {ex.Message}";
    }

    /// <summary>
    /// Card click: plays it (the file is resolved by the player via FilePathResolver —
    /// local match or mp3 from cache/network). Queue = all playable cards of the page,
    /// so Previous/Next walk the whole list. Unavailable — nothing happens.
    /// </summary>
    /// <summary>Anti-double-click: the artwork Play button's command and a MouseLeftButtonUp
    /// bubbling to the card fire the same command twice within milliseconds; the second
    /// call saw "track already playing" and paused it — the click "did not work first time".</summary>
    private VkCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(VkCard? card)
    {
        if (card == null || IsSyncing || IsLoadingTrack) return Task.CompletedTask;
        if (!card.IsPlayable) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Clicking the playing VK card again — pause/resume.
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceVk && current.ScId == card.VkId)
            {
                // Toggle only during live playback: while the track is still opening
                // (stream resolution), PlayPauseToggle broke the open chain.
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Runtime cards are built on click (not stored on the page's cards):
            // FilePath is empty — resolved on every step through the queue.
            var queue = Cards.Where(c => c.IsPlayable)
                             .Select((c, i) => VkRuntimeTracks.BuildRuntimeTrack(
                                 c.VkId, c.Title, c.Artist, c.DurationMs, c.ArtworkLocalPath, i))
                             .ToList();
            var start = queue.First(t => t.ScId == card.VkId);
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
