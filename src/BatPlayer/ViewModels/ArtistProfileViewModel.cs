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
/// Artist profile: header with the name and a grid of the artist's tracks (same card as in the library).
/// Shows the artist's local tracks plus their SoundCloud likes (SC cards come after local ones;
/// the file is resolved at playback via AudioService.FilePathResolver).
/// The playback context queue is the profile's tracks.
/// </summary>
public partial class ArtistProfileViewModel : PageViewModel
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyTracksRepository _spotifyTracks;

    // Callback into MainViewModel: the Back button leads to the Artists page.
    private readonly Action _back;

    private string _artistKey = string.Empty;
    private bool _hasArtist;

    public ObservableCollection<Track> Tracks { get; } = new();

    [ObservableProperty] private string _trackCountText = string.Empty;

    /// <summary>Loading skeleton: set on click (PrepareArtist), cleared when the track list
    /// is rebuilt. Left untouched on in-place refresh (LibraryChanged).</summary>
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

        // VM lives as long as the app, so no unsubscribe is needed.
        Loc.LanguageChanged += (_, _) => RefreshTexts();

        // No need to react to library changes until the profile has been opened at least once.
        _library.LibraryChanged += (_, _) =>
        {
            if (!_hasArtist) return;
            Application.Current?.Dispatcher.Invoke(() => _ = ReloadTracksAsync());
        };
    }

    /// <summary>Synchronous part of opening the profile — runs at click time, before the
    /// transition animation, so the header and skeleton appear with the page. Actual track
    /// loading (ReloadTracksAsync) is deferred until the fade/slide ends
    /// (MainViewModel.LoadAfterTransitionAsync); Clear/Add mid-animation would drop frames.</summary>
    public void PrepareArtist(string artistKey)
    {
        _artistKey = ArtistHelper.Key(artistKey);
        _hasArtist = true;
        IsLoading = true;
        // Hide the previous artist's tracks immediately: the profile reuses a live VM, and
        // without clearing, the previous artist's list stays visible until loading finishes.
        Tracks.Clear();
        RefreshTexts();
    }

    /// <summary>Rebuilds the artist's track list. Public: called from MainViewModel as a
    /// deferred load (after the transition animation) and on LibraryChanged.</summary>
    public async Task ReloadTracksAsync()
    {
        // Fit matching: a track belongs to the artist if AT LEAST ONE of its co-authors
        // (separators: comma, ampersand) yields the searched key.
        bool Matches(string? artist) => ArtistHelper.Split(artist).Any(a => ArtistHelper.Key(a) == _artistKey);

        var tracks = (await _library.GetAllTracksAsync())
            .Where(t => Matches(t.Artist))
            .OrderBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // This artist's SoundCloud likes come after local tracks. FilePath is empty: the
        // player resolves the file on play (local match / cached mp3 / stream).
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

        // VK, Yandex Music and Spotify: runtime cards built from a snapshot; the file is resolved by FilePathResolver.
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

        // Unified "newest first" order regardless of source (like Home): a freshly added
        // track from any platform is not hidden at the end of its block. Sorting is
        // stable — equal dates keep the previous order (local: alphabetical,
        // platform: catalog order).
        tracks = tracks.OrderByDescending(t => t.DateAdded).ToList();

        Tracks.Clear();
        foreach (var t in tracks) Tracks.Add(t);
        TrackCountText = FormatTrackCount();
        IsLoading = false;
    }

    /// <summary>Shuffle the profile's tracks; if a track from this list is playing,
    /// the player queue is rebuilt in the new order.</summary>
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
        // Explicit card click: retry an SC track whose resolve failed earlier in this
        // session (IsAvailable=false) — network/VPN may be back. It is the same object
        // stored in Tracks/queue, so the flag reset is visible to auto-transitions too.
        // (PlayPauseTrack re-enters here.)
        if (track.Source == Track.SourceSoundCloud && !track.IsAvailable)
            track.IsAvailable = true;
        _audio.PlayTrack(track, Tracks);
    }

    /// <summary>Is this the current track. For SC cards we also compare ScId: runtime track
    /// Ids are negative and can collide between different lists.</summary>
    private bool IsCurrentTrack(Track track)
        => _audio.CurrentTrack is Track current
           && track.IsSameTrackAs(current);

    /// <summary>
    /// Play button on the artwork: if this track is playing — pause/resume,
    /// otherwise start it normally.
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
