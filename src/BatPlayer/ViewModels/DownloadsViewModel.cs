using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>
/// Downloads page: the library's local tracks — files added from the computer
/// (Source=local), sorted by date added (newest first). Downloaded SC tracks stay
/// on the SoundCloud tab (likes). Click plays a track; the queue is all local tracks
/// of the page, so Previous/Next walk the whole list.
/// </summary>
public partial class DownloadsViewModel : PageViewModel, ISearchablePage
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;

    public ObservableCollection<Track> Tracks { get; } = new();

    // === Universal search (the bar in the window header) ===
    private List<Track> _allTracks = new();
    private string _searchQuery = string.Empty;

    /// <summary>Filters the list by title and artist; an empty query shows the full list.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildTracks();
    }

    private void RebuildTracks()
    {
        var q = _searchQuery.Trim();
        Tracks.Clear();
        foreach (var t in _allTracks)
            if (q.Length == 0
                || t.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || t.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                Tracks.Add(t);
        RefreshTexts();
    }

    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _hintText = string.Empty;

    /// <summary>Errors for the main window toast (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    public DownloadsViewModel(LibraryService library, AudioService audio)
    {
        _library = library;
        _audio = audio;
        Title = Loc.Get("Downloads");

        // VM lives as long as the app, so no unsubscribe is needed.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Downloads");
            RefreshTexts();
        };
    }

    /// <summary>Called from MainViewModel.Navigate("Downloads"): rebuild the list.</summary>
    public async Task OnNavigatedAsync()
    {
        // Clear the previous cards up front, before the query: otherwise the previous
        // visit's cards stayed visible under the header until loading finished.
        // IsLoading hides the empty state so it does not flash during the clear.
        IsLoading = true;
        Tracks.Clear();
        RefreshTexts();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Local tracks from the DB (Source=local): recently added on top.
            _allTracks = await _library.GetAllTracksAsync(SortColumn.DateAdded, SortDirection.Descending);
            RebuildTracks();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Downloads list load failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Empty page": loading finished and there are no tracks — otherwise the
    /// placeholder would flash during the clear/load.</summary>
    public bool ShowEmptyState => !IsLoading && Tracks.Count == 0;

    /// <summary>Shuffle the downloads list; if a track from this list is playing,
    /// the player queue is rebuilt in the new order.</summary>
    [RelayCommand]
    private void ShuffleTracks() =>
        Helpers.CardsShuffler.Shuffle(Tracks, _audio,
            (t, i) => t, (playing, t) => playing.Id == t.Id && playing.Source == t.Source, "Downloads");

    private void RefreshTexts()
    {
        CounterText = string.Format(Loc.Get("TracksCountFormat"), Tracks.Count);
        HintText = Loc.Get("DownloadsHint");
    }

    /// <summary>
    /// Card click: plays the track (SC cards never reach this page — FilePath is always
    /// set for local ones); clicking the playing track again pauses/resumes.
    /// Queue = the page's local tracks.
    /// </summary>
    [RelayCommand]
    private Task PlayPauseTrack(Track? track)
    {
        if (track == null || string.IsNullOrEmpty(track.FilePath)) return Task.CompletedTask;
        try
        {
            if (_audio.CurrentTrack is Track current && track.IsSameTrackAs(current))
            {
                _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }
            _audio.PlayTrack(track, Tracks);
        }
        catch (Exception ex)
        {
            // The file may have been deleted between building the list and the click.
            Logger.Error(ex, "Downloads play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
