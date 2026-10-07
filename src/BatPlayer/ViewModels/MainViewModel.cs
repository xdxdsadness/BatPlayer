using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;
using BatPlayer.Services.Vk;
using BatPlayer.Services.YandexMusic;
using BatPlayer.Services.Spotify;

namespace BatPlayer.ViewModels;

/// <summary>
/// Main ViewModel. Holds the current page, manages navigation and shared commands.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SettingsService _settings;
    private readonly TrayService _tray;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly SearchService _search;
    private readonly SoundCloudService _soundCloud;
    private readonly Services.SoundCloud.SoundCloudOfficialAuth _scApiAuth;
    private readonly SoundCloudStreamCache _scCache;
    private readonly VkService _vk;
    private readonly VkStreamCache _vkCache;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmService _ym;
    private readonly YmStreamCache _ymCache;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyService _spotify;
    private readonly SpotifyTracksRepository _spotifyTracks;

    // Per-session cache of API track objects (transcodings) by sc_id: the player
    // resolver does not re-issue GET /tracks/{id} on every step through an SC queue.
    private readonly Dictionary<string, ScTrack> _scTrackApiCache = new();

    // Negative cache of SC resolve failures: dead transcodings (DRM/geo/removed) do
    // not "come back to life" within seconds. Repeated clicks on the same card get an
    // instant error — without 5-6 HTTP requests and seconds of waiting per click.
    // We store the EXPIRY MOMENT of the mark: a normal failure — 1 minute, DRM (FairPlay) — 6 hours.
    private readonly Dictionary<string, DateTime> _scResolveFailedAt = new();
    private static readonly TimeSpan ScResolveFailTtl = TimeSpan.FromMinutes(1);

    public PlayerBarViewModel Player { get; }
    public LibraryViewModel Library { get; }
    public ArtistsViewModel Artists { get; }
    public ArtistProfileViewModel ArtistProfile { get; }
    public PlaylistViewModel Playlists { get; }
    public SettingsViewModel Settings { get; }
    public EqualizerViewModel Equalizer { get; }
    public SearchViewModel Search { get; }
    public SoundCloudLikesViewModel SoundCloud { get; }
    public VkMusicViewModel VkMusic { get; }
    public YmMusicViewModel YmMusic { get; }
    public SpotifyMusicViewModel Spotify { get; }
    public WaveViewModel Wave { get; }
    public DownloadsViewModel Downloads { get; }
    public StatsViewModel Stats { get; }

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private string _activePage = "Home";
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isNowPlayingOpen;

    /// <summary>Text of the search bar in the window header. Shared by all pages: on
    /// library pages it goes to Library.SearchText (global search over local tracks and
    /// platform metadata), on the rest — to the active page's ISearchablePage
    /// implementation (filtering its cards).</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    // Search query generation: input debounce (see OnSearchTextChanged).
    private int _searchGeneration;

    partial void OnSearchTextChanged(string value)
    {
        // Debounce 200ms + generation: only the last input's search runs.
        var generation = ++_searchGeneration;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            if (generation != _searchGeneration) return; // input already changed
            Application.Current?.Dispatcher.Invoke(() => RouteSearchToActivePage(SearchText));
        });
    }

    /// <summary>Routes the search query to the active page (on typing and on page change —
    /// the active search text is carried over to each opened page).</summary>
    private void RouteSearchToActivePage(string? query)
    {
        switch (ActivePage)
        {
            case "Home":
            case "Library":
            case "Recent":
            case "RecentlyPlayed":
            case "Favorites":
                Library.SearchText = query ?? string.Empty;
                break;
            default:
                (CurrentPage as ISearchablePage)?.ApplySearch(query);
                break;
        }
    }

    public MainViewModel(LibraryService library, AudioService audio, SettingsService settings,
                        TrayService tray, GlobalHotkeyService hotkeys, SearchService search,
                        PlaylistService playlistService, HistoryService history, CoverCacheService covers,
                        EqualizerService equalizer, MetadataService metadata,
                        SoundCloudService soundCloud, SoundCloudLoginService soundCloudLogin,
                        Services.SoundCloud.SoundCloudOfficialAuth scApiAuth,
                        SoundCloudLikesRepository soundCloudLikes, SoundCloudStreamCache soundCloudCache,
                        VkService vk, VkLoginService vkLogin, VkTracksRepository vkTracks, VkStreamCache vkCache,
                        YmService ym, YmLoginService ymLogin, YmTracksRepository ymTracks, YmStreamCache ymCache,
                        SpotifyService spotify, SpotifyTracksRepository spotifyTracks,
                        RecommendationService wave)
    {
        _library = library;
        _audio = audio;
        _settings = settings;
        _tray = tray;
        _hotkeys = hotkeys;
        _search = search;
        _soundCloud = soundCloud;
        _scApiAuth = scApiAuth;
        _scCache = soundCloudCache;
        _vk = vk;
        _vkCache = vkCache;
        _vkTracks = vkTracks;
        _ym = ym;
        _ymCache = ymCache;
        _ymTracks = ymTracks;
        _spotify = spotify;
        _spotifyTracks = spotifyTracks;

        Player     = new PlayerBarViewModel(audio, library, covers, ym);
        Library    = new LibraryViewModel(library, audio, covers, metadata, soundCloudLikes, vkTracks, ymTracks, spotifyTracks, ym, history);
        Artists    = new ArtistsViewModel(library, soundCloudLikes, vkTracks, ymTracks, spotifyTracks, OpenArtist);
        // "Back" from the profile goes to the Artists page, via normal navigation.
        ArtistProfile = new ArtistProfileViewModel(library, audio, soundCloudLikes, vkTracks, ymTracks, spotifyTracks, () => Navigate("Artists"));
        Playlists  = new PlaylistViewModel(playlistService, library, audio);
        Settings   = new SettingsViewModel(settings, library, audio, soundCloud, soundCloudLogin, soundCloudLikes,
                                           scApiAuth,
                                           vk, vkLogin, vkTracks,
                                           ym, ymLogin, ymTracks,
                                           spotify, spotifyTracks);
        Equalizer  = new EqualizerViewModel(equalizer, audio, settings);
        Search     = new SearchViewModel(search, audio);
        SoundCloud = new SoundCloudLikesViewModel(soundCloud, library, audio, soundCloudLikes, soundCloudCache);
        VkMusic    = new VkMusicViewModel(vk, library, audio, vkTracks);
        YmMusic    = new YmMusicViewModel(ym, library, audio, ymTracks);
        Spotify    = new SpotifyMusicViewModel(spotify, library, audio, spotifyTracks);
        Wave       = new WaveViewModel(wave, ym, soundCloud, library, audio, ymTracks);
        Downloads  = new DownloadsViewModel(library, audio);
        Stats      = new StatsViewModel(history);

        // Grid density applies live: SettingsService notifies on every write.
        settings.SettingsChanged += (_, _) => OnPropertyChanged(nameof(GridColumns));

        // The player can open platform runtime cards (SoundCloud/VK/Yandex Music;
        // FilePath is empty): the path is resolved on every transition (click, Next/Previous) —
        // see ResolvePlatformTrackFileAsync.
        _audio.FilePathResolver = ResolvePlatformTrackFileAsync;

        // SoundCloud reconnect (new cookies after login): the resolve-failure blacklist
        // goes stale — MONETIZE tracks marked "dead" under an expired session must retry
        // immediately, not an hour or six later.
        soundCloud.SessionChanged += (_, _) => _scResolveFailedAt.Clear();

        _audio.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        SoundCloud.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        VkMusic.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        YmMusic.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        Spotify.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        Wave.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        Settings.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);
        Downloads.ErrorOccurred += (_, msg) =>
            ErrorMessage?.Invoke(this, msg);

        // The start page is set EXPLICITLY: the "Home" radio with IsChecked="True" from
        // XAML does not execute its command on initialization, and without this
        // CurrentPage stayed null — the app launched with an EMPTY content area
        // until the first sidebar click.
        CurrentPage = Library;
    }

    public event EventHandler<string>? ErrorMessage;
    public event EventHandler? RequestNowPlaying;

    /// <summary>
    /// AudioService.FilePathResolver: a local path for a track with an empty FilePath.
    /// Audio of platform sources (SoundCloud/VK/Yandex Music) resolves the same way:
    /// 1) local match (MatchHelper) — play the library file offline;
    /// 2) mp3 already in the platform's disk cache — play without network;
    /// 3) otherwise stream via the platform API and download into its cache
    ///    (SC: transcodings by sc_id; VK: temporary link via al_audio;
    ///     YM: download-info by ym_id).
    /// null — the file could not be obtained (AudioService skips the track with a log).
    /// </summary>
    private async Task<string?> ResolvePlatformTrackFileAsync(Track track, CancellationToken ct)
    {
        if (track.Source == Track.SourceSoundCloud)
        {
            return await ResolveScTrackFileAsync(track, ct);
        }
        if (track.Source == Track.SourceVk) return await ResolveVkTrackFileAsync(track, ct);
        if (track.Source == Track.SourceYandex) return await ResolveYmTrackFileAsync(track, ct);
        return track.FilePath;
    }

    private async Task<string?> ResolveScTrackFileAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.ScId)) return null;
        try
        {
            // A recently failed resolve — instant error without network calls.
            if (_scResolveFailedAt.TryGetValue(track.ScId, out var expires)
                && DateTime.UtcNow < expires)
                return null;

            // 1) Offline via the local library (match against local tracks only).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return localMatch.FilePath;
            }

            // 2) Already cached — no network needed (prefer .m4a/AAC when both exist).
            var cachedPath = _scCache.GetExistingCachePath(track.ScId);
            if (cachedPath != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return cachedPath;
            }

            var expectedDurationMs = (long)track.Duration.TotalMilliseconds;

            // Streaming cascade over the set of transcodings: AAC HLS → progressive mp3 →
            // mp3-HLS stitching. Used for both the original and a found DRM-track copy.
            // Audio is always cached under the ORIGINAL's scId (a single card).
            // skipSlowFallbacks — for the DRM original: for AD_SUPPORTED tracks the
            // mp3-HLS transcoding 404s too (verified); a second guaranteed 404 request
            // only adds latency before the copy search.
            async Task<string?> ResolveFromTranscodingsAsync(ScTrack t, bool skipSlowFallbacks = false)
            {
                var aac = await _soundCloud.DownloadHlsAacAsync(t, ct);
                if (aac != null)
                    return await _scCache.SaveTrackBytesAsync(aac, track.ScId, ct, SoundCloudStreamCache.AacExtension);

                var url = await _soundCloud.GetPlayableStreamAsync(t, ct);
                if (url != null)
                    return await _scCache.GetStreamFileAsync(url, track.ScId, ct);

                if (skipSlowFallbacks) return null;
                var hls = await _soundCloud.DownloadHlsMp3Async(t, ct);
                if (hls == null) return null;
                return await _scCache.SaveTrackBytesAsync(hls, track.ScId, ct);
            }

            // 3) OFFICIAL SoundCloud API — the FIRST source (connected via pairing code):
            //    streams work for everything the account can access (incl. Go+), need no
            //    web-session cookies, and do not depend on the unofficial api-v2 404 on
            //    media endpoints. Previously the official path came AFTER metadata and
            //    api-v2 policy checks — expired cookies and SNIPPET/BLOCK cut tracks
            //    off before it ("not everything works").
            var officialAac = await _soundCloud.DownloadOfficialAacAsync(track.ScId, expectedDurationMs, ct);
            if (officialAac != null)
            {
                var savedOfficial = await _scCache.SaveTrackBytesAsync(
                    officialAac, track.ScId, ct, SoundCloudStreamCache.AacExtension);
                _scResolveFailedAt.Remove(track.ScId);
                return savedOfficial;
            }

            // 4) Unofficial cascade (api-v2 cookies) — fallback when the official
            //    connection is missing or gave no stream (network/failure). The audio is
            //    still genuine, from the same sc_id; this path never mixes in foreign versions.
            // Metadata: transcodings by sc_id (session cache or GET /tracks/{id}).
            if (!_scTrackApiCache.TryGetValue(track.ScId, out var scTrack))
            {
                scTrack = await _soundCloud.GetTrackAsync(track.ScId, ct);
                if (scTrack != null) _scTrackApiCache[track.ScId] = scTrack;
            }
            if (scTrack == null || !scTrack.Streamable)
            {
                Logger.Warn($"SC track not resolvable (scId={track.ScId}): api={(scTrack == null ? "null" : "ok")}, streamable={scTrack?.Streamable.ToString() ?? "n/a"}, official={_soundCloud.OfficialApiConnected}");
                _scResolveFailedAt[track.ScId] = DateTime.UtcNow + ScResolveFailTtl;
                return null;
            }

            // Go+ track (SNIPPET) or region-locked (BLOCK): the original's transcodings
            // always 404/are forbidden. BUT re-uploaded copies by other users do not
            // inherit regional restrictions and play natively — the copy is searched
            // together with AD_SUPPORTED cases below (reuploadAvailable = true).

            // 5) Cascade: AAC HLS transcoding (~160 kbps — web-player quality) →
            //    progressive mp3 → mp3-HLS stitching. null — network failure/no transcodings.
            //    For AD_SUPPORTED tracks mp3-HLS is guaranteed 404 (and AAC is not
            //    available openly) — skip the request and look for a copy right after progressive.
            var adSupported = string.Equals(scTrack.MonetizationModel, "AD_SUPPORTED", StringComparison.OrdinalIgnoreCase);
            var policyLocked = scTrack.Policy is "SNIPPET" or "BLOCK";
            var resolved = await ResolveFromTranscodingsAsync(scTrack, skipSlowFallbacks: adSupported || policyLocked);
            if (resolved != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return resolved;
            }

            // 6) Cascade failure — diagnostics by monetization model (verified by
            //    intercepting web-player traffic):
            //    - AD_SUPPORTED: SoundCloud serves such tracks ONLY as an encrypted
            //      CENC stream with a Widevine license (ctr/cbc-encrypted-hls →
            //      playback.media-streaming.soundcloud.cloud + license.widevine).
            //      Regular transcodings 404 for EVERYONE — anonymous or logged in.
            //      The standard path is impossible, BUT SC almost always has re-uploaded
            //      copies of the same track by other users — usually uploaded as regular
            //      tracks and playing natively. Find and play a copy.
            //      Fast path: the original's only chance is the progressive resolve,
            //      already tried above; copies go through the full cascade (all variants live).
            //    - Expired session (401 api-v2) on a regular track: reconnecting the
            //      account fixes it — short TTL so a retry happens right away.
            var sessionDead = _soundCloud.HasAuthFile && await _soundCloud.VerifyWebSessionAsync(ct) == false;

            if (adSupported || policyLocked)
            {
                var candidates = await _soundCloud.SearchReuploadCandidatesAsync(
                    track.Artist, track.Title, (long)track.Duration.TotalMilliseconds, track.ScId, limit: 3, ct);
                foreach (var (altId, altTitle) in candidates)
                {
                    var alt = await _soundCloud.GetTrackAsync(altId.ToString(), ct);
                    // Re-check against full metadata: search-result fields may be
                    // wider/narrower, and the copy may have gone DRM after the search cache.
                    if (alt == null || !alt.Streamable) continue;
                    if (string.Equals(alt.MonetizationModel, "AD_SUPPORTED", StringComparison.OrdinalIgnoreCase)) continue;
                    if (alt.Policy is "SNIPPET" or "BLOCK") continue;

                    Logger.Info($"SC DRM track (scId={track.ScId}) — playing reupload {altId} \"{altTitle}\"");
                    var altResolved = await ResolveFromTranscodingsAsync(alt);
                    if (altResolved != null)
                    {
                        _scResolveFailedAt.Remove(track.ScId);
                        return altResolved;
                    }
                    Logger.Warn($"SC reupload {altId} failed to stream — trying next candidate");
                }

                Logger.Warn($"SC track (scId={track.ScId}, policy={scTrack.Policy}, monetization={scTrack.MonetizationModel}) — no playable reupload found; original is {(adSupported ? "DRM/Widevine-only" : "region/subscription-locked")} for this account");
                _scResolveFailedAt[track.ScId] = DateTime.UtcNow + TimeSpan.FromHours(6);
                ErrorMessage?.Invoke(this, Loc.Get("SoundCloudDrmProtected"));
                return null;
            }

            if (sessionDead)
            {
                Logger.Warn($"SC track unresolvable (scId={track.ScId}, policy={scTrack.Policy}) — web session expired, reconnect SoundCloud in Settings");
                _scResolveFailedAt[track.ScId] = DateTime.UtcNow + ScResolveFailTtl;
                ErrorMessage?.Invoke(this, Loc.Get("SoundCloudSessionExpired"));
                return null;
            }

            var ttl = SoundCloudService.HasEncryptedTranscodings(scTrack)
                ? TimeSpan.FromHours(6)
                : ScResolveFailTtl;
            _scResolveFailedAt[track.ScId] = DateTime.UtcNow + ttl;
            return null;
        }
        catch (Exception ex)
        {
            // The resolve must not crash playback: AudioService will skip the track.
            Logger.Error(ex, "SoundCloud file resolve failed");
            // Partial downloads (DPI cutting the media stream) — probabilistic blocking:
            // start the batched bypass in the background; after it starts, a second click plays.
            if (ex.Message.Contains("media stream stalled", StringComparison.Ordinal))
                _ = BatPlayer.Services.DpiBypass.EnsureStartedAsync(SoundCloudHttp.DpiBypassEnabled);
            if (!string.IsNullOrEmpty(track.ScId))
                _scResolveFailedAt[track.ScId] = DateTime.UtcNow + ScResolveFailTtl;
            return null;
        }
    }

    /// <summary>
    /// File for a VK card: 1) local match; 2) mp3 from the vk_cache;
    /// 3) temporary mp3 link from al_audio (links are not stored in the DB — resolved
    /// via the in-memory session link cache) and download into VkStreamCache.
    /// </summary>
    private async Task<string?> ResolveVkTrackFileAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.ScId)) return null; // ScId holds the vk_id when Source="vk"
        try
        {
            // 1) Offline via the local library (match against local tracks only).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null) return localMatch.FilePath;

            // 2) Already cached — no network needed.
            if (_vkCache.IsTrackCached(track.ScId))
                return _vkCache.GetCacheFilePath(track.ScId);

            // 3) Stream: DB row (metadata) → temporary link → download to cache.
            var vkTrack = await _vkTracks.GetByVkIdAsync(track.ScId);
            if (vkTrack == null)
            {
                Logger.Warn($"VK track not found in database (vkId={track.ScId})");
                return null;
            }

            var streamUrl = await _vk.GetPlayableStreamAsync(vkTrack, ct);
            if (streamUrl == null) return null;

            return await _vkCache.GetStreamFileAsync(streamUrl, track.ScId, ct);
        }
        catch (Exception ex)
        {
            // The resolve must not crash playback: AudioService will skip the track.
            Logger.Error(ex, "VK file resolve failed");
            return null;
        }
    }

    /// <summary>
    /// File for a YM card: 1) local match; 2) mp3 from the ym_cache;
    /// 3) temporary mp3 link from download-info (links are not stored in the DB —
    /// resolved via the in-memory session link cache with TTL) and download into
    /// YmStreamCache.
    /// </summary>
    private async Task<string?> ResolveYmTrackFileAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.ScId)) return null; // ScId holds the ym_id when Source="yandex"

        string? resolved = null;
        try
        {
            // 1) Offline via the local library (match against local tracks only).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null) return localMatch.FilePath;

            // 2) Already cached — no network needed.
            if (_ymCache.IsTrackCached(track.ScId))
                return _ymCache.GetCacheFilePath(track.ScId);

            // 3) Stream: temporary link from download-info (session cache or API) → play
            //    by the link right away (AudioEngine opens http via MediaFoundationReader —
            //    streaming without waiting), while the file is downloaded to cache in the
            //    background for offline replays. Previously playback waited for the full
            //    track download — clicks answered with a second of delay.
            var streamUrl = await _ym.GetStreamUrlAsync(track.ScId, ct);
            if (streamUrl != null)
            {
                _ = PrefetchYmCacheFileAsync(streamUrl, track.ScId);
                resolved = streamUrl;
            }
        }
        catch (YmApiException ex)
        {
            // 401/403 — the session was reset by the service; other codes get the same
            // "do not crash playback" policy.
            Logger.Error(ex, $"Yandex Music file resolve failed (HTTP {ex.HttpCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The resolve must not crash playback: AudioService will skip the track.
            Logger.Error(ex, "Yandex Music file resolve failed");
        }

        return resolved;
    }

    /// <summary>
    /// Background download of a YM track into the disk cache: playback goes by URL
    /// (MediaFoundationReader), and the file is needed for offline replays when the
    /// link expires. Errors go to the log only — playback is unaffected.
    /// </summary>
    private async Task PrefetchYmCacheFileAsync(string streamUrl, string ymId)
    {
        try
        {
            if (_ymCache.IsTrackCached(ymId)) return;
            await _ymCache.GetStreamFileAsync(streamUrl, ymId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Yandex Music background cache download failed ({ymId})");
        }
    }

    /// <summary>Deferred load of the active page: a new navigation cancels the previous
    /// one. Without cancellation a burst of quick menu clicks exploded into a batch of
    /// deferred loads 440 ms later — the UI lagged AFTER the clicks.</summary>
    private CancellationTokenSource? _pageLoadCts;

    [RelayCommand]
    private void Navigate(string page)
    {
        // Repeat click on the current item: the page is already shown, a reload
        // (full list rebuild) only slows things down — ignore. BUT at startup
        // ActivePage is already "Home" (default) while CurrentPage is still null — the
        // first Navigate("Home") from the IsChecked="True" radio MUST create the page,
        // otherwise the app starts with an empty content area.
        if (ActivePage == page && CurrentPage != null) return;

        // Cancel the previous transition's deferred load.
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = new CancellationTokenSource();
        var ct = _pageLoadCts.Token;

        ActivePage = page;
        CurrentPage = page switch
        {
            "Library"      => Library,
            "Playlists"    => Playlists,
            "Settings"     => Settings,
            "Equalizer"    => Equalizer,
            "Artists"      => Artists,
            "SoundCloud"   => SoundCloud,
            "VkMusic"      => VkMusic,
            "YandexMusic"  => YmMusic,
            "Spotify"      => Spotify,
            "Wave"         => Wave,
            "Downloads"    => Downloads,
            "Statistics"   => Stats,
            _              => Library
        };
        // The active search text is carried to the new page: platform grids filter by
        // the same query; for library pages it goes to Library
        // (LoadForCurrentSearchAsync below reads Library.SearchText).
        RouteSearchToActivePage(SearchText);
        // SoundCloud page: re-read cards from the DB; first open — auto-sync.
        // VK Music page and Yandex Music page: the same.
        // Downloads page: rebuild the downloaded-tracks list from the DB + cache.
        // List loading is deferred UNTIL THE END of the transition animation (see
        // LoadAfterTransitionAsync): Clear/Add of hundreds of cards mid fade/slide
        // animation dropped its frames — the transition looked laggy.
        var dispatcher = Application.Current?.Dispatcher;
        if (page == "SoundCloud")
            LoadAfterTransitionAsync(dispatcher, ct, () => SoundCloud.OnNavigatedAsync());
        else if (page == "VkMusic")
            LoadAfterTransitionAsync(dispatcher, ct, () => VkMusic.OnNavigatedAsync());
        else if (page == "YandexMusic")
            LoadAfterTransitionAsync(dispatcher, ct, () => YmMusic.OnNavigatedAsync());
        else if (page == "Spotify")
            LoadAfterTransitionAsync(dispatcher, ct, () => Spotify.OnNavigatedAsync());
        else if (page == "Wave")
            LoadAfterTransitionAsync(dispatcher, ct, () => Wave.OnNavigatedAsync());
        else if (page == "Downloads")
            LoadAfterTransitionAsync(dispatcher, ct, () => Downloads.OnNavigatedAsync());
        else if (page == "Playlists")
            LoadAfterTransitionAsync(dispatcher, ct, () => Playlists.OnNavigatedAsync());
        else if (page == "Statistics")
            LoadAfterTransitionAsync(dispatcher, ct, () => Stats.OnNavigatedAsync());
        // SetFilter — only for library filter pages: "Artists" has its own page
        // (loaded at startup and on LibraryChanged), and the library filter must
        // not be reset when navigating to it.
        else if (page is "Library" or "Home" or "Recent" or "RecentlyPlayed" or "Favorites")
        {
            // Skeleton and header immediately (PrepareFilter), the tracks themselves after the animation.
            Library.PrepareFilter(page);
            LoadAfterTransitionAsync(dispatcher, ct, () => Library.LoadForCurrentSearchAsync());
        }
    }

    /// <summary>
    /// Page data load on navigation: one switch frame is rendered at Background priority,
    /// then we load IMMEDIATELY, without waiting out the animation — lists are collapsed
    /// during loading (IsLoading → Collapsed), so Clear/Add no longer drops fade frames
    /// (see MainWindow.AnimatePageChange).
    /// Cancellation (ct): the user left the page — no load at all, quick menu clicks
    /// do not accumulate work.
    /// </summary>
    private async void LoadAfterTransitionAsync(Dispatcher? dispatcher, CancellationToken ct, Func<Task> load)
    {
        if (ct.IsCancellationRequested) return;

        if (dispatcher == null)
        {
            await load();
            return;
        }

        await dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { }));
        if (ct.IsCancellationRequested) return;

        try
        {
            await load();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Page load failed");
        }
    }

    /// <summary>Opens an artist profile by key (card/name click).</summary>
    [RelayCommand]
    private void OpenArtist(string artistKey)
    {
        // Cancel the previous transition's deferred load — as in Navigate.
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = new CancellationTokenSource();
        var ct = _pageLoadCts.Token;

        // Header and skeleton immediately (PrepareArtist); the list rebuild itself
        // happens AFTER the transition animation, like other pages: Clear/Add of
        // cards mid fade/slide dropped its frames (visible on SC → artist).
        ArtistProfile.PrepareArtist(artistKey ?? string.Empty);
        ActivePage = "ArtistProfile";
        CurrentPage = ArtistProfile;
        LoadAfterTransitionAsync(Application.Current?.Dispatcher, ct, () => ArtistProfile.ReloadTracksAsync());
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = $"{Loc.Get("FileFilterAudio")}|*.mp3;*.wav;*.flac;*.ogg;*.opus;*.aac;*.m4a;*.wma;*.aiff;*.aif|{Loc.Get("FileFilterAll")}|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await Library.AddFilesAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true) return;
        await Library.AddFolderAsync(dlg.FolderName);
        await _library.AddLibraryFolderAsync(dlg.FolderName);
    }

    // ===== Compact grid (8 columns instead of 4) and GIF background =====

    /// <summary>Number of card columns on grid pages (from the GridColumns setting: 4/6/8).</summary>
    public int GridColumns => _settings.Current.GridColumns;

    [RelayCommand]
    private void ToggleGridColumns()
    {
        var next = _settings.Current.GridColumns switch { 4 => 6, 6 => 8, _ => 4 };
        _settings.Update(s => s.GridColumns = next);
        OnPropertyChanged(nameof(GridColumns));
    }

    public bool BackgroundGifEnabled => _settings.Current.BackgroundGifEnabled;
    public string BackgroundGifPath => _settings.Current.BackgroundGifPath;
    public double BackgroundGifOpacity
        => Math.Clamp(_settings.Current.BackgroundGifOpacity, 0, 100) / 100.0;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        _settings.Update(s => s.SidebarCompact = IsSidebarCollapsed);
    }

    [RelayCommand]
    private void OpenNowPlaying()
    {
        IsNowPlayingOpen = !IsNowPlayingOpen;
        RequestNowPlaying?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SearchGlobal()
    {
        Navigate("Library");
        Search.IsActive = true;
    }

    /// <summary>Called from the View when files are dragged in.</summary>
    public async Task HandleDropAsync(string[] paths)
    {
        foreach (var p in paths)
        {
            if (System.IO.Directory.Exists(p))
            {
                await Library.AddFolderAsync(p);
                await _library.AddLibraryFolderAsync(p);
            }
            else if (System.IO.File.Exists(p))
            {
                await Library.AddFilesAsync(new[] { p });
            }
        }
    }

    public async Task InitializeAsync(bool restorePlayback = true)
    {
        _tray.Initialize();
        await Library.LoadAsync();
        // The Artists page is built from the same library — otherwise at startup
        // it stays empty until the first library change.
        await Artists.LoadAsync();
        await Player.RestoreAsync(restorePlayback);
    }
}
