using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
namespace BatPlayer.ViewModels;

public partial class PlaylistViewModel : PageViewModel
{
    private readonly PlaylistService _playlistService;
    private readonly LibraryService _library;
    private readonly AudioService _audio;

    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<Track> SelectedPlaylistTracks { get; } = new();

    [ObservableProperty] private Playlist? _selectedPlaylist;
    [ObservableProperty] private int _totalTrackCount;
    /// <summary>Detail mode: a playlist is open (card grid ↔ its tracks).</summary>
    [ObservableProperty] private bool _isDetailView;

    public PlaylistViewModel(PlaylistService playlistService, LibraryService library, AudioService audio)
    {
        _playlistService = playlistService;
        _library = library;
        _audio = audio;
        Title = Loc.Get("Playlists");

        // VM lives as long as the app — no unsubscribe needed.
        // Re-read the selected playlist's tracks so DisplayArtist gets updated.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Playlists");
            _ = LoadPlaylistTracksAsync();
        };

        // Changing a playlist cover writes cover_path and raises LibraryChanged:
        // re-read the grid, otherwise the new cover would appear only after
        // re-entering the page.
        _library.LibraryChanged += (_, _) => _ = LoadAsync();
    }

    /// <summary>Called from MainViewModel.Navigate("Playlists"): re-read playlists.</summary>
    public async Task OnNavigatedAsync() => await LoadAsync();

    public async Task LoadAsync()
    {
        Playlists.Clear();
        var list = await _library.GetAllPlaylistsAsync();
        foreach (var p in list) Playlists.Add(p);

        // The playlist may have been deleted from detail mode — return to the grid.
        if (SelectedPlaylist != null && Playlists.All(p => p.Id != SelectedPlaylist.Id))
        {
            SelectedPlaylist = null;
            IsDetailView = false;
        }
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = $"{Loc.Get("PlaylistDefaultName")} {Playlists.Count + 1}";
        var id = await _playlistService.CreatePlaylistAsync(name);
        await LoadAsync();
        SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == id);
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        await _library.DeletePlaylistAsync(playlist.Id);
        Playlists.Remove(playlist);
        if (SelectedPlaylist?.Id == playlist.Id)
        {
            SelectedPlaylist = null;
            IsDetailView = false;
        }
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync((Playlist playlist, string newName) args)
    {
        await _library.RenamePlaylistAsync(args.playlist.Id, args.newName);
        await LoadAsync();
    }

    /// <summary>Rename via an input dialog.</summary>
    [RelayCommand]
    private void RenamePlaylistDialog(Playlist? playlist)
    {
        if (playlist == null) return;
        var name = Views.PlaylistDialogs.AskText(playlist.Name, playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name == playlist.Name) return;
        _ = RenamePlaylistAsync((playlist, name));
    }

    /// <summary>Change the playlist cover: pick an image, copy into playlist_covers.</summary>
    [RelayCommand]
    private void ChangeCover(Playlist? playlist)
    {
        if (playlist == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("PlaylistChangeCover"),
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _ = Task.Run(async () =>
        {
            await _library.SetPlaylistCoverAsync(playlist.Id, dlg.FileName);
        });
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        if (value == null) IsDetailView = false;
        _ = LoadPlaylistTracksAsync();
    }

    /// <summary>Open a playlist from the card grid: detail mode with its tracks.</summary>
    [RelayCommand]
    private void OpenPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        SelectedPlaylist = playlist;
        IsDetailView = true;
    }

    /// <summary>Back to the playlist grid.</summary>
    [RelayCommand]
    private void CloseDetailView() => IsDetailView = false;

    /// <summary>Shuffle the playlist's tracks; if a track from this playlist is playing,
    /// the player queue is rebuilt in the new order.</summary>
    [RelayCommand]
    private void ShufflePlaylist() =>
        Helpers.CardsShuffler.Shuffle(SelectedPlaylistTracks, _audio,
            (t, i) => t, (playing, t) => playing.Id == t.Id && playing.Source == t.Source, "Playlist");

    /// <summary>Play a specific playlist track (double-click on a row).
    /// playlistId: this playlist's card shows Pause while its queue plays.</summary>
    [RelayCommand]
    private void PlayTrackFromPlaylist(Track? track)
    {
        if (track == null || SelectedPlaylist == null) return;
        _audio.PlayTrack(track, SelectedPlaylistTracks, SelectedPlaylist.Id);
    }

    private async Task LoadPlaylistTracksAsync()
    {
        SelectedPlaylistTracks.Clear();
        if (SelectedPlaylist == null)
        {
            TotalTrackCount = 0;
            return;
        }
        var tracks = await _library.GetPlaylistTracksAsync(SelectedPlaylist.Id);
        TotalTrackCount = tracks.Count;
        foreach (var t in tracks) SelectedPlaylistTracks.Add(t);
    }

    [RelayCommand]
    private void PlayPlaylist(Playlist playlist)
    {
        _ = PlayPlaylistAsync(playlist);
    }

    /// <summary>Card click: start the queue; for an ALREADY playing playlist —
    /// a real Pause/Resume toggle (like track cards).</summary>
    private async Task PlayPlaylistAsync(Playlist playlist)
    {
        if (_audio.CurrentPlaylistId == playlist.Id && _audio.CurrentTrack != null)
        {
            _audio.PlayPauseToggle();
            return;
        }
        var tracks = await _library.GetPlaylistTracksAsync(playlist.Id);
        if (tracks.Count == 0) return;
        _audio.PlayTrack(tracks[0], tracks, playlist.Id);
    }
}
