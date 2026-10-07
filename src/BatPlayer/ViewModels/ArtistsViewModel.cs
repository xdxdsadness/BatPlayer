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

/// <summary>Artist card on the Artists page.
/// HasSc — the artist has SoundCloud likes (cloud badge on the card).</summary>
public sealed record ArtistCard(
    string Key,
    string DisplayName,
    int TrackCount,
    string? CoverPath,
    string TracksText,
    bool HasSc = false);

/// <summary>
/// Builds artist cards from two lists — local tracks + SoundCloud likes.
/// Static pure function: groups by ArtistHelper.Key; cover is the local one
/// (CoverCachePath), falling back to the like's artwork_local_path; TrackCount is summed.
/// Extracted from ArtistsViewModel so it can be unit-tested without a WPF environment.
/// </summary>
public static class ArtistCardBuilder
{
    public static List<ArtistCard> Build(IEnumerable<Track> localTracks, IEnumerable<SoundCloudLikeRow> scLikes,
        IEnumerable<VkTrackRow>? vkTracks = null, IEnumerable<YmTrackRow>? ymTracks = null,
        IEnumerable<SpotifyTrackRow>? spotifyTracks = null)
    {
        // Key (lowercase) → (track count, cover, has SC likes).
        var byKey = new Dictionary<string, (int Count, string? Cover, bool HasSc)>();
        // First-seen spelling of the name is used for display ("Kai Angel", not "kai angel").
        var display = new Dictionary<string, string>();

        void AddArtist(string? rawArtist, string? cover, bool isSc)
        {
            var names = ArtistHelper.Split(rawArtist);
            if (names.Count == 0) names = new List<string> { string.Empty }; // unknown artist

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

        // VK, Yandex Music and Spotify artists are the same groups: their tracks count
        // alongside local and SC ones (one artist across platforms is merged).
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
/// Artists page: grid of artist cards built by grouping the library's local tracks
/// AND SoundCloud likes by normalized name (ArtistHelper.Key).
/// Cards with SC likes get a cloud badge.
/// </summary>
public partial class ArtistsViewModel : PageViewModel
{
    private readonly LibraryService _library;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyTracksRepository _spotifyTracks;

    // Callback into MainViewModel: opens an artist profile by key.
    private readonly Action<string> _openArtist;

    private List<Track> _allTracks = new();
    private List<SoundCloudLikeRow> _scLikesCache = new();
    private List<VkTrackRow> _vkTracksCache = new();
    private List<YmTrackRow> _ymTracksCache = new();
    private List<SpotifyTrackRow> _spotifyTracksCache = new();

    // Debounce LibraryChanged: a folder scan inserts tracks one by one
    // (InsertOrUpdateTrackAsync raises LibraryChanged per file) — without debouncing
    // the page would rebuild N times in a row. One reload 300 ms after the last event.
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

        // VM lives as long as the app, so no unsubscribe is needed.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Artists");
            // Rebuilding also re-reads localized DisplayName and the "N tracks" text.
            Rebuild();
        };

        _library.LibraryChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Each event restarts the timer: we load once after the burst.
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
            return; // a new event arrived — its fresh timer will reload
        }
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        _allTracks = await _library.GetAllTracksAsync();

        // Likes are an addition to the local library: their failure must not hide artists.
        try
        {
            _scLikesCache = await _scLikes.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes for artists page failed");
            _scLikesCache = new List<SoundCloudLikeRow>();
        }

        // VK/Yandex are similar additions: a sync failure must not hide the other artists.
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

    /// <summary>Groups tracks by the normalized artist name.</summary>
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
