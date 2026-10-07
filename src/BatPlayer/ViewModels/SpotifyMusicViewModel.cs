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
using BatPlayer.Services.Spotify;

namespace BatPlayer.ViewModels;

/// <summary>
/// Spotify track card for the UI (similar to SoundCloudCard).
/// </summary>
public sealed class SpotifyCard
{
    public required string SpotifyId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Artist name for display (ArtistHoverTemplate binds exactly this): an empty
    /// value is replaced by the localized "Unknown artist".</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;
    public required string? ArtworkLocalPath { get; init; }
    public required bool IsPlayable { get; init; }

    /// <summary>Matched track from the local library (null — no match).</summary>
    public Track? LocalTrack { get; init; }

    /// <summary>Whether there is a local-library match.</summary>
    public bool HasLocalMatch => LocalTrack != null;

    /// <summary>Playable: only with a local match.</summary>
    public bool CanPlay => HasLocalMatch;

    /// <summary>Unavailable tracks are dimmed.</summary>
    public double CardOpacity => CanPlay ? 1.0 : 0.45;


    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// Spotify page: grid of Saved Tracks (Liked Songs) from the local DB,
/// sync via the official Spotify Web API, matching with the local library.
///
/// The Spotify API does not allow MP3 streaming — playback only via local matches.
/// </summary>
public partial class SpotifyMusicViewModel : PageViewModel, ISearchablePage
{
    private readonly SpotifyService _spotify;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SpotifyTracksRepository _repository;

    // Local library for matching
    private List<Track> _localTracks = new();

    // Auto-sync on first open
    private bool _autoSyncChecked;

    // Cards already read from the DB: re-entering the page
    // does not rebuild the list (only the sync changes the data).
    private bool _cardsLoaded;

    public ObservableCollection<SpotifyCard> Cards { get; } = new();

    // === Universal search (the bar in the window header) ===
    // The full card list is stored separately: Cards shows either everything or the
    // filtered subset; after a sync/reload the filter is applied again.
    private List<SpotifyCard> _allCards = new();
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
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _lastSyncedText = string.Empty;

    /// <summary>Errors for the main window toast.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Whether a Spotify account is connected.</summary>
    public bool IsConnected => _spotify.HasAuthFile;

    /// <summary>Show the "empty page": loading finished and there are no cards.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    public SpotifyMusicViewModel(SpotifyService spotify, LibraryService library,
                                 AudioService audio, SpotifyTracksRepository repository)
    {
        _spotify = spotify;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = "Spotify";

        Loc.LanguageChanged += (_, _) =>
        {
            Title = "Spotify";
            RefreshHeaderText();
        };

        _spotify.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };
    }

    /// <summary>
    /// Called on page navigation: re-read cards from the DB;
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
            var last = _spotify.GetLastSyncedUtc();
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

            // Matching via a prebuilt index
            var index = MatchHelper.BuildIndex(_localTracks);

            var fresh = new List<SpotifyCard>(rows.Count);
            foreach (var row in rows)
            {
                fresh.Add(new SpotifyCard
                {
                    SpotifyId = row.SpotifyId,
                    Title = row.Title,
                    Artist = row.Artist,
                    Album = row.Album,
                    DurationMs = row.DurationMs,
                    ArtworkUrl = row.ArtworkUrl,
                    ArtworkLocalPath = row.ArtworkLocalPath,
                    IsPlayable = row.IsPlayable,
                    LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                });
            }

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify tracks load failed");
        }
        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Shuffle the page's cards; if a track of this list is playing (local
    /// matches are what play), the player queue is rebuilt in the new order.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing || IsImporting) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.CanPlay && c.LocalTrack != null ? c.LocalTrack : null,
            (playing, c) => playing.SpotifyId == c.SpotifyId,
            "Spotify");
    }

    private void RefreshHeaderText()
    {
        var last = _spotify.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("SoundCloudNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        
        var matched = Cards.Count(c => c.HasLocalMatch);
        CounterText = $"{Cards.Count} tracks ({matched} matched)";
    }

    /// <summary>
    /// Open the Spotify login window.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsSyncing || IsImporting) return;

        var loginWindow = new Views.SpotifyLoginWindow(_spotify) 
        { 
            Owner = System.Windows.Application.Current.MainWindow 
        };

        var result = loginWindow.ShowDialog();
        if (result == true)
        {
            Logger.Info("Spotify connected successfully");
            OnPropertyChanged(nameof(IsConnected));
            await SyncNowAsync();
        }
    }

    /// <summary>
    /// Disconnect the Spotify account (delete tokens).
    /// </summary>
    [RelayCommand]
    private void Disconnect()
    {
        if (IsSyncing || IsImporting) return;

        var result = System.Windows.MessageBox.Show(
            "Disconnect from Spotify and delete all saved tracks?",
            "Spotify",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _spotify.Disconnect();
            _allCards = new List<SpotifyCard>();
            RebuildCards();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ShowEmptyState));
            Logger.Info("Spotify disconnected");
        }
    }

    /// <summary>
    /// Sync Saved Tracks with Spotify.
    /// </summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing || IsImporting) return;
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
            var count = await _spotify.SyncSavedTracksAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
            Logger.Info($"Spotify sync completed: {count} tracks");
        }
        catch (SpotifyApiException ex)
        {
            Logger.Error($"Spotify sync failed: HTTP {ex.StatusCode}");
            ErrorOccurred?.Invoke(this, $"Spotify sync failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify sync failed");
            ErrorOccurred?.Invoke(this, "Spotify sync failed");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Free library import without the Web API (Spotify Development Mode requires
    /// Premium since February 2026): the official "Download your data" export
    /// (ZIP/JSON with YourLibrary.json / Playlist*.json) or CSV from exporters.
    /// Results go into the same spotify_tracks table as the Web API sync.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        if (IsSyncing || IsImporting) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Spotify library",
            Filter = "Spotify data export (*.zip;*.json)|*.zip;*.json|CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        IsImporting = true;
        try
        {
            var rows = await Task.Run(() => SpotifyImportService.ParseFile(dialog.FileName));
            if (rows.Count == 0)
            {
                ErrorOccurred?.Invoke(this, Loc.Get("SpotifyImportNoTracks"));
                return;
            }

            await _repository.UpsertBatchAsync(rows);
            _spotify.SetLastSyncedUtc(DateTime.UtcNow);
            await LoadCardsAsync();

            Logger.Info($"Spotify import completed: {rows.Count} tracks");
            ErrorOccurred?.Invoke(this, string.Format(Loc.CurrentCulture,
                Loc.Get("SpotifyImportDone"), rows.Count));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify import failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("SpotifyImportFailed")}: {ex.Message}");
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Play a Spotify track (only via a local match).
    /// </summary>
    /// <summary>Anti-double-click: the artwork Play button's command and a MouseLeftButtonUp
    /// bubbling to the card fire the same command twice within milliseconds; the second
    /// call saw "track already playing" and paused it — the click "did not work first time".</summary>
    private SpotifyCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(SpotifyCard? card)
    {
        if (card == null || IsSyncing) return Task.CompletedTask;
        if (!card.CanPlay) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Repeat click — pause/resume
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceSpotify && current.SpotifyId == card.SpotifyId)
            {
                // Toggle only during live playback: while the track is still opening
                // (stream resolution), PlayPauseToggle broke the open chain.
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Play the local match
            if (card.LocalTrack != null)
            {
                var queue = Cards.Where(c => c.CanPlay && c.LocalTrack != null)
                                 .Select(c => c.LocalTrack!)
                                 .ToList();
                _audio.PlayTrack(card.LocalTrack, queue);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify play failed");
            ErrorOccurred?.Invoke(this, $"Playback error: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
