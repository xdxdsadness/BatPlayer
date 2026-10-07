using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.Vk;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.ViewModels;

/// <summary>
/// Central content area: track table, filters, sorting.
/// Shows all tracks of the filter; scrolling via scrollbar.
/// </summary>
public partial class LibraryViewModel : PageViewModel
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly CoverCacheService _covers;
    private readonly MetadataService _meta;
    private readonly SoundCloudLikesRepository _scLikes;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyTracksRepository _spotifyTracks;
    private readonly YmService _ym;

    private List<Track> _allTracks = new();

    // Current filter page (Home/Recent/RecentlyPlayed/Favorites) — needed to
    // recompute the title when the language changes.
    private string _page = "Home";

    /// <summary>Current filter page: the empty state in LibraryView draws the icon of the
    /// nav item the page was opened from (Home — house, Favorites — heart), so the bar
    /// background no longer looks "foreign".</summary>
    public string PageKind => _page;

    public ObservableCollection<Track> Tracks { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SortColumn _sortColumn = SortColumn.DateAdded;
    [ObservableProperty] private SortDirection _sortDirection = SortDirection.Descending;
    [ObservableProperty] private ViewMode _viewMode = ViewMode.List;
    [ObservableProperty] private string _filterMode = "All";
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private int _scanTotal;
    [ObservableProperty] private string _scanCurrentFile = string.Empty;
    [ObservableProperty] private bool _isScanning;

    /// <summary>Filter page loading skeleton: set at click time (PrepareFilter), cleared
    /// when LoadAsync finishes. Search and background rebuilds (LibraryChanged, language
    /// change) do not touch it, so the skeleton does not flash while typing or scanning.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>"Empty page": loading finished and there are no tracks — otherwise the
    /// "library empty" placeholder would flash under the skeleton during load.</summary>
    public bool ShowEmptyState => !IsLoading && Tracks.Count == 0;

    /// <summary>Search query generation: stale debounce runs are discarded.</summary>
    private int _searchGeneration;

    public LibraryViewModel(LibraryService library, AudioService audio, CoverCacheService covers, MetadataService meta,
                            SoundCloudLikesRepository scLikes, VkTracksRepository vkTracks,
                            YmTracksRepository ymTracks, SpotifyTracksRepository spotifyTracks, YmService ym,
                            HistoryService? history = null)
    {
        _library = library;
        _audio = audio;
        _covers = covers;
        _meta = meta;
        _scLikes = scLikes;
        _vkTracks = vkTracks;
        _ymTracks = ymTracks;
        _spotifyTracks = spotifyTracks;
        _ym = ym;
        Title = PageTitle(_page);

        // VM lives as long as the app, so no unsubscribe is needed.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = PageTitle(_page);
            // Rebuild the list so DisplayArtist/DisplayAlbum are re-read.
            SetTracks(_allTracks);
        };

        _library.ScanProgress += (_, p) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ScanProgress = p.done;
                ScanTotal = p.total;
                ScanCurrentFile = Path.GetFileName(p.current);
            });
        };

        _library.LibraryChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() => _ = LoadAsync());
        };

        // Live reload of "Recently Played": a new play moves the track to the top and
        // the 50th drops off, without re-entering the page. 500 ms debounce: a play is
        // logged when a track starts, right next to player card changes — the reload
        // must not compete with those frames.
        if (history != null)
        {
            history.PlayLogged += () =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (FilterMode != "Played") return;
                    _playLoggedCts?.Cancel();
                    _playLoggedCts = new CancellationTokenSource();
                    _ = ReloadRecentlyPlayedDebouncedAsync(_playLoggedCts.Token);
                });
            };
        }
    }

    private CancellationTokenSource? _playLoggedCts;

    private async Task ReloadRecentlyPlayedDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (FilterMode != "Played") return;
        await LoadAsync();
    }

    private string PageTitle(string page) => page switch
    {
        "Recent"         => Loc.Get("RecentlyAdded"),
        "RecentlyPlayed" => Loc.Get("RecentlyPlayed"),
        "Favorites"      => Loc.Get("Favorites"),
        _                => Loc.Get("Home"),
    };

    /// <summary>Synchronous part of the filter change — runs at click time, before the
    /// transition animation, so the header and skeleton appear with the page. The actual
    /// load (LoadAsync) is deferred until the fade/slide ends
    /// (MainViewModel.LoadAfterTransitionAsync): Clear/Add of hundreds of cards
    /// mid-animation would drop its frames.</summary>
    public void PrepareFilter(string page)
    {
        _page = page;
        Title = PageTitle(page);
        OnPropertyChanged(nameof(PageKind));
        FilterMode = page switch
        {
            "Home"          => "All",
            "Recent"        => "Recent",
            "RecentlyPlayed"=> "Played",
            "Favorites"     => "Favorites",
            _               => "All"
        };
        IsLoading = true;
        // Clear the previous filter's cards IMMEDIATELY, at click time: the library page
        // is shared by Home/Favorites/RecentlyPlayed, and without this the previous
        // bar's cards stayed visible under the new header until loading finished
        // (service pages do not have this problem — their list lives in its own VM and
        // loads instantly from cached cards). Clean sheet → cards, like Yandex.
        Tracks.Clear();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public void SetFilter(string page)
    {
        PrepareFilter(page);
        // Active search wins over the page filter.
        _ = LoadForCurrentSearchAsync();
    }

    /// <summary>Load honoring the active search (it overrides the page filter):
    /// called as the deferred load after the transition animation.</summary>
    public Task LoadForCurrentSearchAsync()
        => string.IsNullOrWhiteSpace(SearchText) ? LoadAsync() : ApplySearchAsync();

    public async Task LoadAsync()
    {
        // Local tracks, SoundCloud likes and VK music are independent queries: start
        // them all at once and await together (Task.WhenAll) instead of sequential awaits.
        // The "Played" branch reads history with played_at instead of a local List<Track> —
        // merged with platform plays from play_log by time (see Merge).
        Task<List<Track>>? localTask;
        Task<List<LibraryServiceLocalPlay>>? localDatedTask = null;
        switch (FilterMode)
        {
            case "Recent":    localTask = _library.GetRecentlyAddedAsync(); break;
            case "Played":    localTask = null; localDatedTask = _library.GetRecentlyPlayedDatedAsync(); break;
            case "Favorites": localTask = _library.GetFavoritesAsync(); break;
            default:          localTask = _library.GetAllTracksAsync(SortColumn, SortDirection); break;
        }
        // "Recent" (recently added) — local only: likes are not requested at all.
        var likesTask = FilterMode == "Recent"
            ? Task.FromResult<IReadOnlyList<SoundCloudLikeRow>>(new List<SoundCloudLikeRow>())
            : GetScLikesSafeAsync();
        // VK music — "All" (list addition) and "Played" (match against play_log plays):
        // VK has neither add dates ("Recent") nor likes ("Favorites") in this model.
        var vkTask = FilterMode is "All" or "Played"
            ? GetVkRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<VkTrackRow>>(new List<VkTrackRow>());
        // Yandex Music — like VK, plus "Favorites": YM likes are the platform's
        // favorites (complaint: tracks added in YM did not reach favorites).
        var ymTask = FilterMode is "All" or "Played" or "Favorites"
            ? GetYmRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<YmTrackRow>>(new List<YmTrackRow>());
        // Spotify likes — only for matching platform plays from play_log on "Played".
        var isPlayed = string.Equals(FilterMode, "Played", StringComparison.Ordinal);
        var spotifyTask = isPlayed
            ? GetSpotifyRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<SpotifyTrackRow>>(new List<SpotifyTrackRow>());
        // Platform plays (play_log, source != 'local') — only on "Played".
        var platformPlaysTask = isPlayed
            ? GetPlatformPlaysSafeAsync()
            : Task.FromResult<IReadOnlyList<LibraryServicePlatformPlay>>(new List<LibraryServicePlatformPlay>());

        var allTasks = new List<Task> { likesTask, vkTask, ymTask, spotifyTask, platformPlaysTask };
        if (localTask != null) allTasks.Add(localTask);
        if (localDatedTask != null) allTasks.Add(localDatedTask);
        await Task.WhenAll(allTasks);

        // The final list is built ENTIRELY before Tracks.Clear(): card building is a
        // single synchronous pass with no intermediate awaits. Previously SC likes were
        // appended to Tracks after the local Clear/Add, so the list (and all cards)
        // were rebuilt twice — a noticeable freeze when navigating the sidebar.
        List<Track> full;
        if (localDatedTask != null)
        {
            // "Played": local history + platform plays from play_log, honestly sorted
            // by play time. Previously platform tracks (track.Id < 0, absent from tracks)
            // never made this list, and SC was just the first 50 likes regardless of
            // whether they were played; now the list reflects actual plays.
            full = MergeRecentlyPlayed(localDatedTask.Result, platformPlaysTask.Result,
                likesTask.Result, vkTask.Result, ymTask.Result, spotifyTask.Result);
        }
        else if (string.Equals(FilterMode, "Favorites", StringComparison.Ordinal))
        {
            // "Favorites": unified "newest first" order by add time — YM/SC likes by
            // like time, local favorites by file add date. Complaint: freshly liked
            // tracks "did not get added" — they landed at the end/middle of the long
            // list below other platforms' tracks.
            var ym = YmRuntimeTracks.BuildYmAppend(FilterMode, ymTask.Result, 0);
            full = new List<Track>(ym);
            full.AddRange(localTask!.Result);
            full.AddRange(SoundCloudRuntimeTracks.BuildScAppend(FilterMode, likesTask.Result, ym.Count));
            full = SortByAddedDescending(full);
        }
        else
        {
            full = new List<Track>(localTask!.Result);
            // "Played" — the 50 freshest likes (liked_at DESC), "Favorites"/"All" (Home) —
            // all of them, "Recent" — none (see BuildScAppend).
            full.AddRange(SoundCloudRuntimeTracks.BuildScAppend(FilterMode, likesTask.Result));
            // VK — only on "All"; runtime-Id numbering continues after the SC cards
            // so negative Ids do not collide within one list.
            full.AddRange(VkRuntimeTracks.BuildVkAppend(FilterMode, vkTask.Result, likesTask.Result.Count));
            // Yandex Music — after VK; runtime-Id numbering continues after SC+VK.
            full.AddRange(YmRuntimeTracks.BuildYmAppend(FilterMode, ymTask.Result,
                likesTask.Result.Count + vkTask.Result.Count));
            // Unified "newest first" order INDEPENDENT of source: a freshly added track
            // from any platform rises above older tracks of the others (previously the
            // list was rigid blocks local→SC→VK→YM, and a new track only rose within
            // its own block). Sorting is stable: equal dates keep the previous block
            // order, and within a block — the platform's own order
            // (date_added DESC / liked_at DESC / VK catalog order).
            if (string.Equals(FilterMode, "All", StringComparison.Ordinal))
                full = SortByAddedDescending(full);
        }

        SetTracks(full);
        IsLoading = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Limit of the "Recently Played" page — same as GetRecentlyPlayedAsync.</summary>
    private const int RecentlyPlayedLimit = 50;

    /// <summary>
    /// Merge for "Recently Played": local tracks (history) and platform plays
    /// (play_log, source != 'local') are ordered by played_at descending and the first
    /// RecentlyPlayedLimit are taken. Platform rows are converted to runtime cards via
    /// an exact (Title, Artist) match against their repository (a play row in the DB
    /// holds only a metadata snapshot); without a match the row is skipped. FilePath
    /// stays empty — the file is resolved at playback via AudioService.FilePathResolver.
    /// </summary>
    private List<Track> MergeRecentlyPlayed(
        List<LibraryServiceLocalPlay> local,
        IReadOnlyList<LibraryServicePlatformPlay> platformPlays,
        IReadOnlyList<SoundCloudLikeRow> likes,
        IReadOnlyList<VkTrackRow> vkRows,
        IReadOnlyList<YmTrackRow> ymRows,
        IReadOnlyList<SpotifyTrackRow> spotifyRows)
    {
        // Index by (Title, Artist): a Dictionary over a tuple compares elements
        // ordinally — exactly the "exact match" needed. With duplicates in the
        // catalog the first row wins (like FirstOrDefault).
        var likeByKey = IndexByTitleArtist(likes, l => (l.Title, l.Artist));
        var vkByKey = IndexByTitleArtist(vkRows, r => (r.Title, r.Artist));
        var ymByKey = IndexByTitleArtist(ymRows, r => (r.Title, r.Artist));
        var spotifyByKey = IndexByTitleArtist(spotifyRows, r => (r.Title, r.Artist));

        // Runtime-Id numbering continues across all platform cards of the list
        // so negative Ids do not collide (SpotifyRuntimeTracks writes the Id as given,
        // so it is passed an already negative value).
        var runtimeIndex = 0;
        var platform = new List<(Track Track, string PlayedAt)>(platformPlays.Count);
        foreach (var p in platformPlays)
        {
            // Priority — the saved platform_id (the track is recognizable without the
            // catalog: this is how WAVE plays, absent from likes, reach Recently Played).
            // Fallback for old rows without an id — exact (Title, Artist) match against the catalog.
            Track? card = p.Source switch
            {
                Track.SourceSoundCloud => !string.IsNullOrEmpty(p.PlatformId)
                    ? SoundCloudRuntimeTracks.BuildRuntimeTrack(p.PlatformId, p.Title, p.Artist,
                        p.DurationMs, p.ArtworkPath, runtimeIndex++)
                    : likeByKey.TryGetValue((p.Title, p.Artist), out var l)
                        ? SoundCloudRuntimeTracks.BuildRuntimeTrack(l.ScId, l.Title, l.Artist, l.DurationMs,
                            l.ArtworkLocalPath, runtimeIndex++)
                        : null,
                Track.SourceVk => !string.IsNullOrEmpty(p.PlatformId)
                    ? VkRuntimeTracks.BuildRuntimeTrack(p.PlatformId, p.Title, p.Artist,
                        p.DurationMs, p.ArtworkPath, runtimeIndex++)
                    : vkByKey.TryGetValue((p.Title, p.Artist), out var v)
                        ? VkRuntimeTracks.BuildRuntimeTrack(v.VkId, v.Title, v.Artist, v.DurationMs,
                            v.ArtworkLocalPath, runtimeIndex++)
                        : null,
                Track.SourceYandex => !string.IsNullOrEmpty(p.PlatformId)
                    ? YmRuntimeTracks.BuildRuntimeTrack(p.PlatformId, p.Title, p.Artist,
                        p.DurationMs, p.ArtworkPath, available: true, runtimeIndex++)
                    : ymByKey.TryGetValue((p.Title, p.Artist), out var y)
                        ? YmRuntimeTracks.BuildRuntimeTrack(y.YmId, y.Title, y.Artist, y.DurationMs,
                            y.ArtworkLocalPath, y.Available, runtimeIndex++)
                        : null,
                Track.SourceSpotify => !string.IsNullOrEmpty(p.PlatformId)
                    ? SpotifyRuntimeTracks.BuildRuntimeTrack(p.PlatformId, p.Title, p.Artist,
                        p.DurationMs, p.ArtworkPath, -1 - runtimeIndex++)
                    : spotifyByKey.TryGetValue((p.Title, p.Artist), out var s)
                        ? SpotifyRuntimeTracks.BuildRuntimeTrack(s.SpotifyId, s.Title, s.Artist, s.DurationMs,
                            s.ArtworkLocalPath, -1 - runtimeIndex++)
                        : null,
                _ => null
            };
            if (card != null) platform.Add((card, p.PlayedAt));
        }

        var merged = new List<(Track Track, string PlayedAt)>(local.Count + platform.Count);
        merged.AddRange(local.Select(l => (l.Track, l.PlayedAt)));
        merged.AddRange(platform);

        // played_at is ISO 8601 UTC (DateTime.UtcNow.ToString("o")); unparseable values
        // (e.g. NULL in ancient history rows) go to the very top of the list.
        return merged
            .OrderByDescending(x => ParsePlayedAt(x.PlayedAt))
            .Take(RecentlyPlayedLimit)
            .Select(x => x.Track)
            .ToList();
    }

    private static Dictionary<(string Title, string Artist), T> IndexByTitleArtist<T>(
        IEnumerable<T> rows, Func<T, (string Title, string Artist)> key)
    {
        var dict = new Dictionary<(string Title, string Artist), T>();
        foreach (var row in rows)
        {
            var k = key(row);
            if (!dict.ContainsKey(k)) dict[k] = row;
        }
        return dict;
    }

    private static DateTime ParsePlayedAt(string playedAt)
        => DateTime.TryParse(playedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.MinValue;

    /// <summary>
    /// Unified "newest first" order of the merged list by add date regardless of source
    /// (Track.DateAdded: local — date_added, platform — like/first-sync time, see
    /// TrackTimestamps). LINQ sorting is stable: equal dates keep the block build order.
    /// </summary>
    private static List<Track> SortByAddedDescending(List<Track> tracks)
        => tracks.OrderByDescending(t => t.DateAdded).ToList();

    /// <summary>Likes are an addition to the local library: their failure must not hide local tracks.
    /// The likes repository is read exactly once per LoadAsync (ApplySearchAsync has its own
    /// single query).</summary>
    private async Task<IReadOnlyList<SoundCloudLikeRow>> GetScLikesSafeAsync()
    {
        try
        {
            return await _scLikes.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes append to library list failed");
            return new List<SoundCloudLikeRow>();
        }
    }

    /// <summary>VK music is an addition to the local library: a failure must not hide
    /// local tracks (or SC likes). Read once per LoadAsync.</summary>
    private async Task<IReadOnlyList<VkTrackRow>> GetVkRowsSafeAsync()
    {
        try
        {
            return await _vkTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK tracks append to library list failed");
            return new List<VkTrackRow>();
        }
    }

    /// <summary>Yandex Music is an addition to the local library: a failure must not
    /// hide local tracks (or SC/VK). Read once per LoadAsync.</summary>
    private async Task<IReadOnlyList<YmTrackRow>> GetYmRowsSafeAsync()
    {
        try
        {
            return await _ymTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music tracks append to library list failed");
            return new List<YmTrackRow>();
        }
    }

    /// <summary>Spotify likes (for matching play_log plays on "Played"): a failure must
    /// not hide local tracks and other platforms.</summary>
    private async Task<IReadOnlyList<SpotifyTrackRow>> GetSpotifyRowsSafeAsync()
    {
        try
        {
            return await _spotifyTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify tracks for recently played merge failed");
            return new List<SpotifyTrackRow>();
        }
    }

    /// <summary>Platform plays from play_log: a failure must not hide the local history.</summary>
    private async Task<IReadOnlyList<LibraryServicePlatformPlay>> GetPlatformPlaysSafeAsync()
    {
        try
        {
            return await _library.GetRecentlyPlayedPlatformAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Platform play log for recently played merge failed");
            return new List<LibraryServicePlatformPlay>();
        }
    }

    /// <summary>Full track set of the filter.</summary>
    private void SetTracks(IEnumerable<Track> tracks)
    {
        _allTracks = tracks.ToList();
        Tracks.Clear();
        foreach (var t in _allTracks) Tracks.Add(t);
        // The empty state depends on the count too: a rebuild without loading
        // (language change) must recompute it as well.
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public async Task AddFilesAsync(string[] files)
    {
        IsScanning = true;
        ScanTotal = files.Length;
        ScanProgress = 0;
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                ScanCurrentFile = Path.GetFileName(files[i]);
                ScanProgress = i + 1;

                if (!Helpers.FileHelpers.IsAudioFile(files[i]))
                {
                    Logger.Warn($"Skipping non-audio file: {files[i]}");
                    continue;
                }

                var t = await _meta.ReadAsync(files[i]);
                if (t != null)
                {
                    await _library.InsertOrUpdateTrackAsync(t);
                    // cache the cover now so it shows up in the freshly loaded list
                    if (!string.IsNullOrEmpty(t.CoverHash))
                        await _covers.GetOrCreateCoverAsync(t.FilePath, t.CoverHash);
                }
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AddFilesAsync failed");
        }
        finally
        {
            IsScanning = false;
            ScanCurrentFile = string.Empty;
        }
    }

    public async Task AddFolderAsync(string folder)
    {
        IsScanning = true;
        try
        {
            await _library.ScanFolderAsync(folder);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AddFolderAsync failed");
        }
        finally
        {
            IsScanning = false;
            ScanCurrentFile = string.Empty;
        }
    }

    [RelayCommand]
    private Task PlayTrack(Track? track) => PlayTrackCoreAsync(track);

    /// <summary>Shuffle the page list; if a track from this list is playing, the player
    /// queue is rebuilt in the new order (platform queue — cards of the same source,
    /// local — playable files of the shuffled list).</summary>
    [RelayCommand]
    private void ShuffleTracks()
    {
        if (Tracks.Count < 2) return;
        var current = _audio.CurrentTrack;
        var shuffled = Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        Tracks.Clear();
        foreach (var t in shuffled) Tracks.Add(t);
        if (current == null) return;

        var playingCard = Tracks.FirstOrDefault(IsCurrentTrack);
        if (playingCard == null) return; // playing outside this list — leave the queue alone

        if (playingCard.IsPlatformTrack)
        {
            var q = Tracks.Where(t => t.Source == playingCard.Source).ToList();
            _audio.SetQueue(q, Math.Max(0, q.IndexOf(playingCard)));
        }
        else
        {
            var q = Tracks.Where(t => !string.IsNullOrEmpty(t.FilePath)).ToList();
            var idx = q.FindIndex(t => t.Id == playingCard.Id);
            _audio.SetQueue(q, idx < 0 ? 0 : idx);
        }
        Logger.Info($"Library ({FilterMode}): list shuffled, queue rebuilt");
    }


    /// <summary>
    /// Play button on the artwork: if this track is playing — pause/resume,
    /// otherwise start it normally.
    /// </summary>
    [RelayCommand]
    private Task PlayPauseTrack(Track? track) => PlayPauseTrackCoreAsync(track);

    private async Task PlayPauseTrackCoreAsync(Track? track)
    {
        if (track == null) return;
        if (IsCurrentTrack(track))
        {
            _audio.PlayPauseToggle();
            return;
        }
        await PlayTrackCoreAsync(track);
    }

    /// <summary>Is this the current track. For platform cards (SC/VK) we also compare
    /// source and ScId: runtime track Ids are negative and can collide between
    /// different lists (Home/platform pages).</summary>
    private bool IsCurrentTrack(Track track)
        => _audio.CurrentTrack is Track current
           && track.IsSameTrackAs(current);

    private async Task PlayTrackCoreAsync(Track? track)
    {
        if (track == null) return;
        if (track.IsPlatformTrack)
        {
            // Queue = all cards of this platform in the current Home list in display
            // order: Previous/Next walk the whole list; files (local match or cached
            // mp3) are resolved on play via AudioService.FilePathResolver.
            var platformQueue = _allTracks.Where(t => t.Source == track.Source).ToList();
            _audio.PlayTrack(track, platformQueue);
            return;
        }
        _audio.PlayTrack(track, PlayableContextQueue());
    }

    /// <summary>
    /// Context queue without SC cards with an empty FilePath: the player cannot open
    /// a not-yet-resolved file, and Next onto such a position would kill playback.
    /// </summary>
    private IEnumerable<Track> PlayableContextQueue()
    {
        IEnumerable<Track> source = _allTracks.Count > 0 ? _allTracks : Tracks;
        return source.Where(t => !string.IsNullOrEmpty(t.FilePath));
    }

    [RelayCommand]
    private void AddToQueue(Track? track)
    {
        if (track == null) return;
        // A platform card (SC/VK) without a resolved file is not added to the queue:
        // the player could not open it.
        if (track.IsPlatformTrack && string.IsNullOrEmpty(track.FilePath)) return;
        _audio.AddToQueue(track);
    }

    [RelayCommand]
    private async Task RemoveFromLibraryAsync(Track track)
    {
        if (track == null) return;
        await _library.RemoveTrackFromLibraryAsync(track.Id);
        RemoveTrack(track);
    }

    [RelayCommand]
    private async Task DeleteFromDiskAsync(Track track)
    {
        if (track == null) return;
        if (await _library.DeleteFileFromDiskAsync(track.Id))
            RemoveTrack(track);
    }

    private void RemoveTrack(Track track)
    {
        _allTracks.Remove(track);
        Tracks.Remove(track);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(Track track)
    {
        if (track == null) return;

        // Liking a YM card goes to the Yandex Music ACCOUNT (like the player heart):
        // a local UPDATE by negative runtime id finds no row, the flag only changed
        // in memory and was lost on page reload.
        if (track.Source == Track.SourceYandex)
        {
            var target = !track.IsFavorite;
            if (!await _ym.SetTrackLikedAsync(track.ScId, target))
                return; // API did not confirm — do not toggle the heart
            track.IsFavorite = target;
            return;
        }

        // Other platform runtime cards (VK/SC/Spotify): no likes in this model.
        if (track.Id <= 0) return;

        track.IsFavorite = !track.IsFavorite;
        await _library.SetFavoriteAsync(track.Id, track.IsFavorite);
    }

    partial void OnSearchTextChanged(string value)
    {
        // Debounce 200ms + query generation: with fast typing the delays fire in
        // sequence but only the last input's search runs — stale runs (and their DB
        // reads) are discarded.
        var generation = ++_searchGeneration;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            if (generation != _searchGeneration) return; // input already changed
            Application.Current?.Dispatcher.Invoke(() => _ = ApplySearchAsync(generation));
        });
    }

    private async Task ApplySearchAsync(int generation = 0)
    {
        if (generation != 0 && generation != _searchGeneration) return;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await LoadAsync();
            return;
        }
        var tracks = await _library.GetAllTracksAsync(SortColumn, SortDirection);

        // Search overrides the page filter and also covers platform metadata —
        // SoundCloud likes, VK and Yandex Music tracks (title/artist): matches become
        // runtime cards at the end of the list, the file is resolved on click. Each
        // repository is read once here; one platform's failure must not hide local results.
        List<SoundCloudLikeRow> likes = new();
        try
        {
            likes = await _scLikes.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes for search failed");
        }

        List<VkTrackRow> vkRows = new();
        try
        {
            vkRows = await _vkTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK tracks for search failed");
        }

        List<YmTrackRow> ymRows = new();
        try
        {
            ymRows = await _ymTracks.GetAllAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music tracks for search failed");
        }

        var filtered = SoundCloudRuntimeTracks.FilterWithSoundCloud(tracks, likes, SearchText, startIndex: 0);
        // VK matches come after SC; runtime-Id numbering continues after the already
        // built cards so negative Ids do not collide.
        filtered.AddRange(VkRuntimeTracks.FilterWithVk(filtered.Count, vkRows, SearchText));
        // Yandex Music matches come after VK (numbering continues).
        filtered.AddRange(YmRuntimeTracks.FilterWithYm(filtered.Count, ymRows, SearchText));
        // The same unified "newest first" order as on the page without search:
        // platform matches land by their own add date rather than as chunks at the end.
        SetTracks(SortByAddedDescending(filtered));
    }
}
