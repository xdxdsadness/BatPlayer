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
/// Главная ViewModel. Хранит текущую страницу, управляет навигацией, общими командами.
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

    // track-объекты API (transcodings) по sc_id за сессию: резолвер плеера не дёргает
    // GET /tracks/{id} на каждый переход по SC-очереди.
    private readonly Dictionary<string, ScTrack> _scTrackApiCache = new();

    // Негативный кэш резолва SC-треков: мёртвые транскодинги (DRM/гео/удалённые) не
    // «оживают» за секунды. Повторные клики по той же карточке отдаём мгновенной
    // ошибкой — без 5-6 HTTP-запросов и пары секунд ожидания на каждый клик.
    // Храним МОМЕНТ ИСТЕЧЕНИЯ пометки: обычный отказ — 1 минута, DRM (FairPlay) — 6 часов.
    private readonly Dictionary<string, DateTime> _scResolveFailedAt = new();
    private static readonly TimeSpan ScResolveFailTtl = TimeSpan.FromMinutes(1);
    private readonly YtFallbackService _ytFallback = new();

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

    /// <summary>Текст поисковой строки в шапке окна. Единая на все страницы: на
    /// страницах библиотеки уходит в Library.SearchText (глобальный поиск по локальным
    /// трекам и платформенным метаданным), на остальных — в реализацию ISearchablePage
    /// активной страницы (фильтр её карточек).</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    // Поколение поискового запроса: дебаунс ввода (см. OnSearchTextChanged).
    private int _searchGeneration;

    partial void OnSearchTextChanged(string value)
    {
        // Debounce 200ms + поколение: выполняется только поиск последнего ввода.
        var generation = ++_searchGeneration;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            if (generation != _searchGeneration) return; // ввод уже изменился
            Application.Current?.Dispatcher.Invoke(() => RouteSearchToActivePage(SearchText));
        });
    }

    /// <summary>Маршрут поискового запроса активной странице (при вводе и при смене страницы —
    /// активный текст поиска переносится на каждую открываемую страницу).</summary>
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
        // «Назад» из профиля — на страницу «Исполнители», через обычную навигацию.
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

        // Плотность сетки применяется живо: SettingsService уведомляет о каждой записи.
        settings.SettingsChanged += (_, _) => OnPropertyChanged(nameof(GridColumns));

        // Плеер умеет открывать платформенные runtime-карточки (SoundCloud/VK/Яндекс Музыка;
        // FilePath пуст): путь резолвится на каждом переходе (клик, Next/Previous) —
        // см. ResolvePlatformTrackFileAsync.
        _audio.FilePathResolver = ResolvePlatformTrackFileAsync;

        // Переподключение SoundCloud (новые cookies после входа): чёрный список
        // провалов резолва устаревает — MONETIZE-треки, помеченные «мёртвыми» при
        // протухшей сессии, должны ретраиться сразу, а не через час-шесть.
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

        // Прогресс обхода блокировки SoundCloud (перебор стратегий zapret) — индикатор
        // в тайтл-баре: пользователь видит, ПОЧЕМУ SC-трек «задумался» и что обход жив.
        Services.SoundCloud.SoundCloudZapret.UiStateChanged += OnZapretUiStateChanged;

        // Стартовая страница задаётся ЯВНО: радио «Home» с IsChecked="True" из XAML свою
        // команду при инициализации не исполняет, и без этого CurrentPage оставался null —
        // приложение запускалось с ПУСТОЙ контентной областью до первого клика по сайдбару.
        CurrentPage = Library;

#if DEBUG
        // Отладка вёрстки индикатора без реального перебора: BATPLAYER_DEBUG_BADGE=1
        // показывает плашку «поиск стратегии» сразу после старта (prod не задевает).
        if (Environment.GetEnvironmentVariable("BATPLAYER_DEBUG_BADGE") == "1")
        {
            IsBypassBusy = true;
            BypassStatusText = Loc.Get("ZapretSearching");
        }
#endif
    }

    // ===== Индикатор обхода блокировки SoundCloud =====

    /// <summary>Текст индикатора обхода в тайтл-баре; null — индикатор скрыт.</summary>
    [ObservableProperty] private string? _bypassStatusText;

    /// <summary>true, пока идёт перебор стратегий — индикатор крутит спиннер.</summary>
    [ObservableProperty] private bool _isBypassBusy;

    /// <summary>Отложенное скрытие индикатора после финального сообщения (найдено/не удалось).</summary>
    private CancellationTokenSource? _bypassHideCts;

    private void OnZapretUiStateChanged(Services.SoundCloud.SoundCloudZapret.ZapretUiState state)
    {
        // События летят из фонового цикла перебора — весь UI-стейт только в UI-потоке.
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyZapretUiState(state));
    }

    private void ApplyZapretUiState(Services.SoundCloud.SoundCloudZapret.ZapretUiState state)
    {
        CancelBypassHide();
        switch (state.Phase)
        {
            case Services.SoundCloud.SoundCloudZapret.ZapretPhase.Starting:
                IsBypassBusy = true;
                BypassStatusText = Loc.Get("ZapretSearching");
                break;
            case Services.SoundCloud.SoundCloudZapret.ZapretPhase.TestingPreset:
                IsBypassBusy = true;
                BypassStatusText = string.Format(Loc.Get("ZapretStrategy"), state.PresetNumber, state.PresetCount);
                break;
            case Services.SoundCloud.SoundCloudZapret.ZapretPhase.Succeeded:
                IsBypassBusy = false;
                BypassStatusText = string.Format(Loc.Get("ZapretFound"), state.PresetName);
                HideBypassAfter(TimeSpan.FromSeconds(6));
                break;
            case Services.SoundCloud.SoundCloudZapret.ZapretPhase.Failed:
                IsBypassBusy = false;
                BypassStatusText = Loc.Get("ZapretFailed");
                HideBypassAfter(TimeSpan.FromSeconds(6));
                break;
            case Services.SoundCloud.SoundCloudZapret.ZapretPhase.HelperDeclined:
                IsBypassBusy = false;
                BypassStatusText = Loc.Get("ZapretNeedUac");
                HideBypassAfter(TimeSpan.FromSeconds(6));
                break;
        }
    }

    private void HideBypassAfter(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        _bypassHideCts = cts;
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_bypassHideCts, cts)) return;
                BypassStatusText = null;
                _bypassHideCts = null;
            });
        });
    }

    private void CancelBypassHide()
    {
        _bypassHideCts?.Cancel();
        _bypassHideCts = null;
    }

    public event EventHandler<string>? ErrorMessage;
    public event EventHandler? RequestNowPlaying;

    /// <summary>
    /// AudioService.FilePathResolver: локальный путь для трека с пустым FilePath.
    /// Звук платформенных источников (SoundCloud/VK/Яндекс Музыка) резолвится одинаково:
    /// 1) локальный матч (MatchHelper) — играем файл библиотеки офлайн;
    /// 2) mp3 уже в дисковом кэше платформы — играем без сети;
    /// 3) иначе стрим через API платформы и докачка в её кэш
    ///    (SC: transcodings по sc_id; VK: временная ссылка al_audio;
    ///     YM: download-info по ym_id).
    /// null — получить файл не удалось (AudioService пропустит трек с логом).
    /// </summary>
    private async Task<string?> ResolvePlatformTrackFileAsync(Track track, CancellationToken ct)
    {
        if (track.Source == Track.SourceSoundCloud)
        {
            var sc = await ResolveScTrackFileAsync(track, ct);
            if (sc != null) return sc;

            // SC не отдал файл (DRM FairPlay / policy / 404 / гео): фолбэк — YouTube
            // и дальше играется локально; метаданные в UI остаются от SC.
            // Условие: официальный API должен быть не просто «подключён», а РАБОТАТЬ.
            // Раньше фолбэк глушился одним фактом подключения — и когда SoundCloud
            // заблокировал клиента целиком (403 disallowed на всё), монетизированные/
            // Go+-треки вставали намертво: официальный путь мёртв, а YouTube-фолбэк
            // был запрещён («при живом официальном API звук — настоящий SC-стрим»).
            // Каскад SC и так всегда первый: фолбэк добирает только то, что ВСЕ
            // SC-источники отказались играть.
            if (_settings.Current.YouTubeFallbackEnabled && !_soundCloud.OfficialApiUsable)
                return await _ytFallback.ResolveMp3Async(
                    track.Artist, track.Title, track.ScId, track.Duration, ct);
            return null;
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
            // Недавно провалившийся резолв — мгновенная ошибка без походов в сеть.
            if (_scResolveFailedAt.TryGetValue(track.ScId, out var expires)
                && DateTime.UtcNow < expires)
                return null;

            // 1) Офлайн через локальную библиотеку (матч только по локальным трекам).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return localMatch.FilePath;
            }

            // 2) Уже в кэше — сети не нужно (предпочитаем .m4a/AAC, если есть оба).
            var cachedPath = _scCache.GetExistingCachePath(track.ScId);
            if (cachedPath != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return cachedPath;
            }

            var expectedDurationMs = (long)track.Duration.TotalMilliseconds;

            // Каскад стриминга по набору транскодингов: AAC HLS → progressive mp3 →
            // склейка mp3-HLS. Используется и для оригинала, и для найденной копии
            // DRM-трека. Аудио всегда кэшируется под scId ОРИГИНАЛА (карточка одна).
            // skipSlowFallbacks — для DRM-оригинала: у AD_SUPPORTED треков mp3-HLS
            // транскодинг тоже 404ит (проверено), второй гарантированный 404-запрос
            // только добавляет задержку перед поиском копии.
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

            // 3) ОФИЦИАЛЬНЫЙ SoundCloud API — ПЕРВЫЙ источник (подключён pairing-кодом):
            //    стримы работают для всего, что доступно аккаунту (вкл. Go+), не требуют
            //    cookies веб-сессии и не зависят от 404 неофициального api-v2 на
            //    медиа-эндпоинтах. Раньше официальный путь стоял ПОСЛЕ метаданных и
            //    policy-проверки api-v2 — протухшие cookies и SNIPPET/BLOCK отсекали
            //    треки до него («работают не все»).
            var officialAac = await _soundCloud.DownloadOfficialAacAsync(track.ScId, expectedDurationMs, ct);
            if (officialAac != null)
            {
                var savedOfficial = await _scCache.SaveTrackBytesAsync(
                    officialAac, track.ScId, ct, SoundCloudStreamCache.AacExtension);
                _scResolveFailedAt.Remove(track.ScId);
                return savedOfficial;
            }

            // 4) Неофициальный каскад (cookies api-v2) — фолбэк, когда официальное
            //    подключение отсутствует или не отдало стрим (сеть/сбой). Аудио и здесь
            //    настоящее, с того же sc_id, чужих версий этот путь не подмешивает.
            // Метаданные: transcodings по sc_id (кэш сессии или GET /tracks/{id}).
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

            // Go+-трек (SNIPPET) или заблокированный в регионе (BLOCK): у оригинала
            // транскодинги всегда 404/запрещены. НО перекачанные другими пользователями
            // копии того же трека не наследуют региональных ограничений и играют нативно —
            // ищем копию вместе с AD_SUPPORTED-кейсами ниже (reuploadAvailable = true).

            // 5) Каскад: AAC-транскодинг HLS (~160 kbps — качество веб-плеера) →
            //    progressive mp3 → склейка mp3-HLS. null — сеть не дала/транскодингов нет.
            //    У AD_SUPPORTED треков mp3-HLS гарантированно 404 (а AAC и так нет в
            //    открытом виде) — не тратим запрос, копию ищем сразу после progressive.
            var adSupported = string.Equals(scTrack.MonetizationModel, "AD_SUPPORTED", StringComparison.OrdinalIgnoreCase);
            var policyLocked = scTrack.Policy is "SNIPPET" or "BLOCK";
            var resolved = await ResolveFromTranscodingsAsync(scTrack, skipSlowFallbacks: adSupported || policyLocked);
            if (resolved != null)
            {
                _scResolveFailedAt.Remove(track.ScId);
                return resolved;
            }

            // 6) Провал каскада — диагностика по модели монетизации (проверено
            //    перехватом трафика веб-плеера):
            //    - AD_SUPPORTED: SoundCloud отдаёт такие треки ТОЛЬКО зашифрованным
            //      CENC-стримом с лицензией Widevine (ctr/cbc-encrypted-hls →
            //      playback.media-streaming.soundcloud.cloud + license.widevine).
            //      Обычные transcodings 404ят у ВСЕХ — и анонимно, и залогиненным.
            //      Штатный путь невозможен, НО на SC почти всегда есть перекачанные
            //      другими пользователями копии того же трека — они обычно залиты
            //      как обычные треки и играют нативно. Ищем и играем копию.
            //      Быстрый путь: единственный шанс оригинала — progressive-резолв,
            //      он уже был выше; для копий каскад полный (у них все варианты живы).
            //    - Истекшая сессия (401 api-v2) у обычного трека: переподключение
            //      аккаунта чинит — короткий TTL, чтобы ретрай случился сразу.
            var sessionDead = _soundCloud.HasAuthFile && await _soundCloud.VerifyWebSessionAsync(ct) == false;

            if (adSupported || policyLocked)
            {
                var candidates = await _soundCloud.SearchReuploadCandidatesAsync(
                    track.Artist, track.Title, (long)track.Duration.TotalMilliseconds, track.ScId, limit: 3, ct);
                foreach (var (altId, altTitle) in candidates)
                {
                    var alt = await _soundCloud.GetTrackAsync(altId.ToString(), ct);
                    // Перепроверка по полным метаданным: у выдачи поиска поля могут
                    // быть шире/уже, а копия могла уйти в DRM после кэша поиска.
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
            // Резолв не должен ронять воспроизведение: AudioService пропустит трек.
            Logger.Error(ex, "SoundCloud file resolve failed");
            // Зависание/обрыв скачивания (DPI режет медиа-поток) — вероятностная
            // блокировка: запускаем пакетную антиблокировку (winws) фоново; после её
            // старта повторный клик играет.
            if (ex.Message.Contains("media stream stalled", StringComparison.Ordinal))
                _ = Services.SoundCloud.SoundCloudZapret.EnsureStartedAsync(SoundCloudHttp.ZapretEnabled);
            if (!string.IsNullOrEmpty(track.ScId))
                _scResolveFailedAt[track.ScId] = DateTime.UtcNow + ScResolveFailTtl;
            return null;
        }
    }

    /// <summary>
    /// Файл для VK-карточки: 1) локальный матч; 2) mp3 из кэша vk_cache;
    /// 3) временная mp3-ссылка из al_audio (ссылки в БД не хранятся — разрешаются
    /// через кэш ссылок в памяти сессии) и докачка в VkStreamCache.
    /// </summary>
    private async Task<string?> ResolveVkTrackFileAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.ScId)) return null; // ScId хранит vk_id при Source="vk"
        try
        {
            // 1) Офлайн через локальную библиотеку (матч только по локальным трекам).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null) return localMatch.FilePath;

            // 2) Уже в кэше — сети не нужно.
            if (_vkCache.IsTrackCached(track.ScId))
                return _vkCache.GetCacheFilePath(track.ScId);

            // 3) Стрим: строка из БД (метаданные) → временная ссылка → качаем в кэш.
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
            // Резолв не должен ронять воспроизведение: AudioService пропустит трек.
            Logger.Error(ex, "VK file resolve failed");
            return null;
        }
    }

    /// <summary>
    /// Файл для YM-карточки: 1) локальный матч; 2) mp3 из кэша ym_cache;
    /// 3) временная mp3-ссылка из download-info (ссылки в БД не хранятся — разрешаются
    /// через кэш ссылок в памяти сессии с TTL) и докачка в YmStreamCache;
    /// что у SoundCloud-резолва; настройка YouTubeFallbackEnabled общая с SC): тарифная
    /// недоступность/регион не должны обрывать волну и очередь страницы.
    /// </summary>
    private async Task<string?> ResolveYmTrackFileAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.ScId)) return null; // ScId хранит ym_id при Source="yandex"

        string? resolved = null;
        try
        {
            // 1) Офлайн через локальную библиотеку (матч только по локальным трекам).
            var localMatch = MatchHelper.FindLocalMatch(
                await _library.GetAllTracksAsync(), track.Artist, track.Title);
            if (localMatch != null) return localMatch.FilePath;

            // 2) Уже в кэше — сети не нужно.
            if (_ymCache.IsTrackCached(track.ScId))
                return _ymCache.GetCacheFilePath(track.ScId);

            // 3) Стрим: временная ссылка из download-info (кэш сессии или API) → играем
            //    по ссылке сразу (AudioEngine открывает http через MediaFoundationReader —
            //    стрим без ожидания), а файл докачивается в кэш фоном для офлайн-повторов.
            //    Раньше playback ждал полной закачки трека — клик отвечал с задержкой в сек.
            var streamUrl = await _ym.GetStreamUrlAsync(track.ScId, ct);
            if (streamUrl != null)
            {
                _ = PrefetchYmCacheFileAsync(streamUrl, track.ScId);
                resolved = streamUrl;
            }
        }
        catch (YmApiException ex)
        {
            // 401/403 — сессия сброшена сервисом; остальным кодам та же политика
            Logger.Error(ex, $"Yandex Music file resolve failed (HTTP {ex.HttpCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Резолв не должен ронять воспроизведение: AudioService пропустит трек.
            Logger.Error(ex, "Yandex Music file resolve failed");
        }

        // 4) Фолбэк: YM не отдал стрим — YouTube по «исполнитель + название».
        if (resolved == null && _settings.Current.YouTubeFallbackEnabled)
            return await _ytFallback.ResolveMp3Async(
                track.Artist, track.Title, track.ScId, track.Duration, ct);
        return resolved;
    }

    /// <summary>
    /// Фоновая докачка YM-трека в дисковый кэш: воспроизведение идёт по URL
    /// (MediaFoundationReader), а файл нужен для офлайн-повторов, когда ссылка
    /// протухнет. Ошибка только в лог — на воспроизведение не влияет.
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

    /// <summary>Отложенная загрузка активной страницы: новый переход отменяет
    /// предыдущую. Без отмены серия быстрых кликов по пунктам меню взрывалась
    /// пачкой отложенных загрузок через 440 мс — UI провисал уже ПОСЛЕ кликов.</summary>
    private CancellationTokenSource? _pageLoadCts;

    [RelayCommand]
    private void Navigate(string page)
    {
        // Повторный клик по текущему пункту: страница уже показана, перезагрузка
        // (полная пересборка списка) только тормозит — игнорируем. НО при старте
        // ActivePage уже «Home» (дефолт), а CurrentPage ещё null — первый
        // Navigate("Home") от радио с IsChecked="True" ОБЯЗАН создать страницу,
        // иначе приложение запускается с пустой контентной областью.
        if (ActivePage == page && CurrentPage != null) return;

        // Гасим отложенную загрузку прежнего перехода.
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
        // Активный текст поиска переносится на новую страницу: платформенные сетки
        // фильтруются тем же запросом, для страниц библиотеки он уходит в Library
        // (LoadForCurrentSearchAsync ниже читает именно Library.SearchText).
        RouteSearchToActivePage(SearchText);
        // Страница SoundCloud: перечитать карточки из БД; первое открытие — авто-синк.
        // Страница VK Music и страница Яндекс Музыки: то же самое.
        // Страница «Загрузки»: пересобрать список скачанных треков из БД + кэша.
        // Загрузка списков откладывается ДО КОНЦА анимации перехода (см.
        // LoadAfterTransitionAsync): Clear/Add сотен карточек посреди fade/slide
        // анимации роняли её кадры — переход выглядел лагающим.
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
        // SetFilter — только для страниц-фильтров библиотеки: у «Artists» своя страница
        // (грузится на старте и по LibraryChanged), и сбрасывать фильтр библиотеки
        // при переходе на неё нельзя.
        else if (page is "Library" or "Home" or "Recent" or "RecentlyPlayed" or "Favorites")
        {
            // Скелетон и заголовок — сразу (PrepareFilter), сами треки — после анимации.
            Library.PrepareFilter(page);
            LoadAfterTransitionAsync(dispatcher, ct, () => Library.LoadForCurrentSearchAsync());
        }
    }

    /// <summary>
    /// Загрузка данных страницы при переходе: один кадр переключения отрисовывается
    /// на Background-приоритете, затем грузим СРАЗУ, без выжидания анимации — списки
    /// на время загрузки свёрнуты (IsLoading → Collapsed), Clear/Add больше не роняют
    /// кадры fade (см. MainWindow.AnimatePageChange).
    /// Отмена (ct): пользователь ушёл с страницы — загрузка не выполняется вовсе,
    /// быстрые клики по меню не копят работу.
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

    /// <summary>Открывает профиль исполнителя по ключу (клик по карточке/имени).</summary>
    [RelayCommand]
    private void OpenArtist(string artistKey)
    {
        // Гасим отложенную загрузку прежнего перехода — как в Navigate.
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = new CancellationTokenSource();
        var ct = _pageLoadCts.Token;

        // Заголовок и скелетон — сразу (PrepareArtist), сама перестройка списка —
        // ПОСЛЕ анимации перехода, как у остальных страниц: Clear/Add карточек
        // посреди fade/slide ронял её кадры (заметно на переходе SC → артист).
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

    // ===== Компактная сетка (8 колонок вместо 4) и GIF-фон =====

    /// <summary>Число колонок карточек на страницах-сетках (по настройке GridColumns: 4/6/8).</summary>
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

    /// <summary>Вызывается из View при перетаскивании файлов.</summary>
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
        // Страница «Исполнители» строится из той же библиотеки — иначе при старте
        // она пустая до первого изменения библиотеки.
        await Artists.LoadAsync();
        await Player.RestoreAsync(restorePlayback);
    }
}
