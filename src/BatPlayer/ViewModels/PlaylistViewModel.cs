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
    /// <summary>Детальный режим: открыт плейлист (сетка карточек ↔ его треки).</summary>
    [ObservableProperty] private bool _isDetailView;

    public PlaylistViewModel(PlaylistService playlistService, LibraryService library, AudioService audio)
    {
        _playlistService = playlistService;
        _library = library;
        _audio = audio;
        Title = Loc.Get("Playlists");

        // VM живёт столько же, сколько приложение — отписка не нужна.
        // Перечитываем треки выбранного плейлиста, чтобы DisplayArtist обновился.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Playlists");
            _ = LoadPlaylistTracksAsync();
        };

        // Смена обложки плейлиста пишет cover_path и уведомляет LibraryChanged:
        // перечитываем сетку, иначе новая обложка появилась бы только после
        // повторного захода на страницу.
        _library.LibraryChanged += (_, _) => _ = LoadAsync();
    }

    /// <summary>Вызывается из MainViewModel.Navigate("Playlists"): перечитать плейлисты.</summary>
    public async Task OnNavigatedAsync() => await LoadAsync();

    public async Task LoadAsync()
    {
        Playlists.Clear();
        var list = await _library.GetAllPlaylistsAsync();
        foreach (var p in list) Playlists.Add(p);

        // Плейлист мог быть удалён из детального режима — вернуться к сетке.
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

    /// <summary>Переименование через диалог ввода.</summary>
    [RelayCommand]
    private void RenamePlaylistDialog(Playlist? playlist)
    {
        if (playlist == null) return;
        var name = Views.PlaylistDialogs.AskText(playlist.Name, playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name == playlist.Name) return;
        _ = RenamePlaylistAsync((playlist, name));
    }

    /// <summary>Смена обложки плейлиста: выбор картинки, копия в playlist_covers.</summary>
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

    /// <summary>Открыть плейлист из сетки карточек: детальный режим с его треками.</summary>
    [RelayCommand]
    private void OpenPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        SelectedPlaylist = playlist;
        IsDetailView = true;
    }

    /// <summary>Назад к сетке плейлистов.</summary>
    [RelayCommand]
    private void CloseDetailView() => IsDetailView = false;

    /// <summary>Перемешать треки плейлиста; если играет трек из этого плейлиста —
    /// очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShufflePlaylist() =>
        Helpers.CardsShuffler.Shuffle(SelectedPlaylistTracks, _audio,
            (t, i) => t, (playing, t) => playing.Id == t.Id && playing.Source == t.Source, "Playlist");

    /// <summary>Играть конкретный трек плейлиста (двойной клик по строке).
    /// playlistId: карточка этого плейлиста показывает Pause, пока играет его очередь.</summary>
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

    /// <summary>Клик по карточке плейлиста: старт очереди, а для УЖЕ играющего
    /// плейлиста — честный тоггл Pause/Resume (как у карточек треков).</summary>
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
