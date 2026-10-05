using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>
/// Профиль исполнителя: шапка с именем и сетка его треков (та же карточка, что в библиотеке).
/// Показываются локальные треки артиста + его лайки SoundCloud (SC-карточки идут после
/// локальных; файл резолвится при воспроизведении через AudioService.FilePathResolver).
/// Контекстная очередь воспроизведения — треки профиля.
/// </summary>
public partial class ArtistProfileViewModel : PageViewModel
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyTracksRepository _spotifyTracks;

    // Колбэк в MainViewModel: кнопка «Назад» ведёт на страницу «Исполнители».
    private readonly Action _back;

    private string _artistKey = string.Empty;
    private bool _hasArtist;

    public ObservableCollection<Track> Tracks { get; } = new();

    [ObservableProperty] private string _trackCountText = string.Empty;

    /// <summary>Скелетон загрузки: ставится в момент клика (PrepareArtist), снимается,
    /// когда список треков перестроен. При обновлении на месте (LibraryChanged) не трогается.</summary>
    [ObservableProperty] private bool _isLoading;

    public ArtistProfileViewModel(LibraryService library, AudioService audio,
                                  SoundCloudLikesRepository scLikes,
                                  VkTracksRepository vkTracks, YmTracksRepository ymTracks,
                                  SpotifyTracksRepository spotifyTracks, Action back)
    {
        _library = library;
        _audio = audio;
        _scLikes = scLikes;
        _vkTracks = vkTracks;
        _ymTracks = ymTracks;
        _spotifyTracks = spotifyTracks;
        _back = back;

        // VM живёт столько же, сколько приложение, поэтому отписка не нужна.
        Loc.LanguageChanged += (_, _) => RefreshTexts();

        // Пока профиль ни разу не открывали, реагировать на изменения библиотеки не нужно.
        _library.LibraryChanged += (_, _) =>
        {
            if (!_hasArtist) return;
            Application.Current?.Dispatcher.Invoke(() => _ = ReloadTracksAsync());
        };
    }

    /// <summary>Синхронная часть открытия профиля — вызывается в момент клика, до анимации
    /// перехода: заголовок и скелетон появляются вместе со страницей. Сама загрузка треков
    /// (ReloadTracksAsync) откладывается до конца fade/slide (MainViewModel.LoadAfterTransitionAsync),
    /// иначе Clear/Add карточек посреди анимации ронял её кадры.</summary>
    public void PrepareArtist(string artistKey)
    {
        _artistKey = ArtistHelper.Key(artistKey);
        _hasArtist = true;
        IsLoading = true;
        // Треки прежнего артиста гасим сразу: профиль открывается на живой VM, и без
        // очистки до конца загрузки был виден список предыдущего артиста.
        Tracks.Clear();
        RefreshTexts();
    }

    /// <summary>Открывает профиль: нормализует ключ исполнителя, строит заголовок и список треков.</summary>
    public async Task SetArtistAsync(string artistKey)
    {
        PrepareArtist(artistKey);
        await ReloadTracksAsync();
    }

    /// <summary>Перестраивает список треков артиста. Публично: вызывается из MainViewModel
    /// как отложенная загрузка (после анимации перехода) и по LibraryChanged.</summary>
    public async Task ReloadTracksAsync()
    {
        // Фит-матчинг: трек принадлежит артисту, если ХОТЯ БЫ ОДИН из его соавторов
        // (разделители: запятая, амперсанд) даёт искомый ключ.
        bool Matches(string? artist) => ArtistHelper.Split(artist).Any(a => ArtistHelper.Key(a) == _artistKey);

        var tracks = (await _library.GetAllTracksAsync())
            .Where(t => Matches(t.Artist))
            .OrderBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Лайки SoundCloud этого артиста — после локальных. FilePath пуст: плеер
        // резолвит файл на переходе (локальный матч / mp3 из кэша / стрим).
        try
        {
            var scTracks = (await _scLikes.GetAllAsync())
                .Where(l => Matches(l.Artist))
                .Select((l, i) => SoundCloudRuntimeTracks.BuildRuntimeTrack(l, l.ArtworkLocalPath, i))
                .ToList();
            tracks.AddRange(scTracks);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes for artist profile failed");
        }

        // VK, Яндекс Музыка и Spotify: runtime-карточки по снимку, файл резолвит FilePathResolver.
        try
        {
            var vk = (await _vkTracks.GetAllAsync())
                .Where(t => Matches(t.Artist))
                .Select((t, i) =>
                {
                    var rt = Helpers.VkRuntimeTracks.BuildRuntimeTrack(
                        t.VkId, t.Title, t.Artist, t.DurationMs, t.ArtworkLocalPath, i);
                    return rt;
                })
                .ToList();
            tracks.AddRange(vk);

            var ym = (await _ymTracks.GetAllAsync())
                .Where(t => Matches(t.Artist))
                .Select((t, i) => Helpers.YmRuntimeTracks.BuildRuntimeTrack(
                    t.YmId, t.Title, t.Artist, t.DurationMs, t.ArtworkLocalPath, t.Available, i))
                .ToList();
            tracks.AddRange(ym);

            var spotify = (await _spotifyTracks.GetAllAsync())
                .Where(t => Matches(t.Artist))
                .Select((t, i) => Helpers.SpotifyRuntimeTracks.BuildRuntimeTrack(
                    t.SpotifyId, t.Title, t.Artist, t.DurationMs, t.ArtworkLocalPath, i,
                    Helpers.TrackTimestamps.ParseUtc(t.AddedAt)))
                .ToList();
            tracks.AddRange(spotify);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK/Yandex/Spotify tracks for artist profile failed");
        }

        // Единый порядок «новые сверху» независимо от источника (как на Home):
        // свежедобавленный трек любой платформы не прячется в конце своего блока.
        // Сортировка стабильна — при равных датах сохраняется прежний порядок
        // (локальные — по алфавиту, платформенные — в порядке каталога).
        tracks = tracks.OrderByDescending(t => t.DateAdded).ToList();

        Tracks.Clear();
        foreach (var t in tracks) Tracks.Add(t);
        TrackCountText = FormatTrackCount();
        IsLoading = false;
    }

    /// <summary>Перемешать треки профиля; если играет трек из этого списка —
    /// очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShuffleTracks() =>
        Helpers.CardsShuffler.Shuffle(Tracks, _audio,
            (t, i) => t, (playing, t) => IsCurrentTrack(t), "Artist profile");

    private void RefreshTexts()
    {
        Title = ArtistHelper.DisplayName(_artistKey);
        TrackCountText = FormatTrackCount();
    }

    private string FormatTrackCount() => string.Format(Loc.Get("TracksCountFormat"), Tracks.Count);

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        if (track == null) return;
        // Явный клик по карточке: SC-трек, проваливший резолв ранее в этой сессии
        // (IsAvailable=false), пробуем снова — сеть/VPN могли вернуться. Объект тот же,
        // что лежит в Tracks/очереди, поэтому сброс флага виден и авто-переходам.
        // (PlayPauseTrack переигрывает сюда же.)
        if (track.Source == Track.SourceSoundCloud && !track.IsAvailable)
            track.IsAvailable = true;
        _audio.PlayTrack(track, Tracks);
    }

    /// <summary>Текущий ли это трек. Для SC-карточек сравниваем ещё и ScId: Id у runtime-треков
    /// отрицательные и могут совпасть между разными списками.</summary>
    private bool IsCurrentTrack(Track track)
        => _audio.CurrentTrack is Track current
           && track.IsSameTrackAs(current);

    /// <summary>
    /// Клик по кнопке Play на обложке: если этот трек сейчас играет — пауза/возобновление,
    /// иначе — обычный запуск трека.
    /// </summary>
    [RelayCommand]
    private void PlayPauseTrack(Track track)
    {
        if (track == null) return;
        if (IsCurrentTrack(track))
            _audio.PlayPauseToggle();
        else
            PlayTrack(track);
    }

    [RelayCommand]
    private void Back() => _back();
}
