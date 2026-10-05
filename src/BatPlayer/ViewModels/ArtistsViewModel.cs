using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>Карточка исполнителя на странице «Исполнители».
/// HasSc — у артиста есть лайки SoundCloud (значок облачка на карточке).</summary>
public sealed record ArtistCard(
    string Key,
    string DisplayName,
    int TrackCount,
    string? CoverPath,
    string TracksText,
    bool HasSc = false);

/// <summary>
/// Сборка карточек артистов из двух списков — локальные треки + лайки SoundCloud.
/// Статическая чистая функция: группировка по ArtistHelper.Key, обложка — локальная
/// (CoverCachePath), а если её нет — artwork_local_path лайка; TrackCount суммируется.
/// Вынесена из ArtistsViewModel, чтобы покрываться юнит-тестами без WPF-окружения.
/// </summary>
public static class ArtistCardBuilder
{
    public static List<ArtistCard> Build(IEnumerable<Track> localTracks, IEnumerable<SoundCloudLikeRow> scLikes,
        IEnumerable<VkTrackRow>? vkTracks = null, IEnumerable<YmTrackRow>? ymTracks = null,
        IEnumerable<SpotifyTrackRow>? spotifyTracks = null)
    {
        // Ключ (нижний регистр) → (количество треков, обложка, есть ли SC-лайки).
        var byKey = new Dictionary<string, (int Count, string? Cover, bool HasSc)>();
        // Первое увиденное написание имени — для отображения ("Kai Angel", не "kai angel").
        var display = new Dictionary<string, string>();

        void AddArtist(string? rawArtist, string? cover, bool isSc)
        {
            var names = ArtistHelper.Split(rawArtist);
            if (names.Count == 0) names = new List<string> { string.Empty }; // неизвестный артист

            foreach (var name in names)
            {
                var key = ArtistHelper.Key(name);
                var cur = byKey.GetValueOrDefault(key);
                byKey[key] = (cur.Count + 1, cur.Cover ?? NonEmpty(cover), cur.HasSc || isSc);
                if (key.Length > 0 && !display.ContainsKey(key)) display[key] = name;
            }
        }

        foreach (var t in localTracks) AddArtist(t.Artist, t.CoverCachePath, false);
        foreach (var like in scLikes) AddArtist(like.Artist, like.ArtworkLocalPath, true);

        // Артисты VK, Яндекс Музыки и Spotify — те же группы: треки считаются наравне
        // с локальными и SC (один артист на разных платформах склеивается).
        foreach (var t in vkTracks ?? Enumerable.Empty<VkTrackRow>())
            AddArtist(t.Artist, t.ArtworkLocalPath, false);

        foreach (var t in ymTracks ?? Enumerable.Empty<YmTrackRow>())
            AddArtist(t.Artist, t.ArtworkLocalPath, false);

        foreach (var t in spotifyTracks ?? Enumerable.Empty<SpotifyTrackRow>())
            AddArtist(t.Artist, t.ArtworkLocalPath, false);

        return byKey
            .Select(kv => new ArtistCard(
                kv.Key,
                display.GetValueOrDefault(kv.Key, ArtistHelper.DisplayName(kv.Key)),
                kv.Value.Count,
                kv.Value.Cover,
                string.Format(Loc.Get("TracksCountFormat"), kv.Value.Count),
                kv.Value.HasSc))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        static string? NonEmpty(string? path) => string.IsNullOrEmpty(path) ? null : path;
    }
}

/// <summary>
/// Страница «Исполнители»: сетка карточек артистов, построенных группировкой
/// локальных треков библиотеки И лайков SoundCloud по нормализованному имени
/// (ArtistHelper.Key). Карточки с SC-лайками помечаются облачком.
/// </summary>
public partial class ArtistsViewModel : PageViewModel
{
    private readonly LibraryService _library;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyTracksRepository _spotifyTracks;

    // Колбэк в MainViewModel: открывает профиль исполнителя по ключу.
    private readonly Action<string> _openArtist;

    private List<Track> _allTracks = new();
    private List<SoundCloudLikeRow> _scLikesCache = new();
    private List<VkTrackRow> _vkTracksCache = new();
    private List<YmTrackRow> _ymTracksCache = new();
    private List<SpotifyTrackRow> _spotifyTracksCache = new();

    // Debounce LibraryChanged: скан папки вставляет треки по одному
    // (InsertOrUpdateTrackAsync дёргает LibraryChanged на каждый файл) — без гашения
    // страница пересобирается N раз подряд. Одна перезагрузка через 300 мс после
    // последнего события.
    private const int LibraryChangedDebounceMs = 300;
    private CancellationTokenSource? _reloadDebounceCts;

    public ObservableCollection<ArtistCard> Artists { get; } = new();

    public ArtistsViewModel(LibraryService library, SoundCloudLikesRepository scLikes,
                            VkTracksRepository vkTracks, YmTracksRepository ymTracks,
                            SpotifyTracksRepository spotifyTracks, Action<string> openArtist)
    {
        _library = library;
        _scLikes = scLikes;
        _vkTracks = vkTracks;
        _ymTracks = ymTracks;
        _spotifyTracks = spotifyTracks;
        _openArtist = openArtist;
        Title = Loc.Get("Artists");

        // VM живёт столько же, сколько приложение, поэтому отписка не нужна.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Artists");
            // Пересборка заодно перечитывает локализованные DisplayName и «N треков».
            Rebuild();
        };

        _library.LibraryChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Каждое событие перезапускает таймер: грузимся один раз после пачки.
                _reloadDebounceCts?.Cancel();
                _reloadDebounceCts = new CancellationTokenSource();
                _ = DebouncedLoadAsync(_reloadDebounceCts.Token);
            });
        };
    }

    private async Task DebouncedLoadAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LibraryChangedDebounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return; // пришло новое событие — перезагрузит свежий таймер
        }
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        _allTracks = await _library.GetAllTracksAsync();

        // Лайки — дополнение к локальной библиотеке: их сбой не должен прятать артистов.
        try
        {
            _scLikesCache = await _scLikes.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes for artists page failed");
            _scLikesCache = new List<SoundCloudLikeRow>();
        }

        // VK/Яндекс — такие же дополнения: сбой синка не прячет остальных артистов.
        try
        {
            _vkTracksCache = await _vkTracks.GetAllAsync();
            _ymTracksCache = await _ymTracks.GetAllAsync();
            _spotifyTracksCache = await _spotifyTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK/Yandex/Spotify tracks for artists page failed");
            _vkTracksCache = new List<VkTrackRow>();
            _ymTracksCache = new List<YmTrackRow>();
            _spotifyTracksCache = new List<SpotifyTrackRow>();
        }

        Rebuild();
    }

    /// <summary>Группировка треков по нормализованному имени исполнителя.</summary>
    private void Rebuild()
    {
        var cards = ArtistCardBuilder.Build(_allTracks, _scLikesCache, _vkTracksCache, _ymTracksCache, _spotifyTracksCache);

        Artists.Clear();
        foreach (var card in cards) Artists.Add(card);
    }

    [RelayCommand]
    private void OpenArtist(ArtistCard? card)
    {
        if (card == null) return;
        _openArtist(card.Key);
    }
}
