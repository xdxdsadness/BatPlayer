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
/// Содержимое центральной области: таблица треков, фильтры, сортировка.
/// Показываются все треки фильтра; прокрутка — скроллом.
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

    // Текущая страница фильтра (Home/Recent/RecentlyPlayed/Favorites) — нужна,
    // чтобы пере-вычислить заголовок при смене языка.
    private string _page = "Home";

    /// <summary>Текущая страница-фильтр: пустое состояние LibraryView рисует иконку
    /// того же пункта навигации, с которого открыта страница (Home — дом, Favorites —
    /// сердце), — задний фон бара больше не «чужой».</summary>
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

    /// <summary>Скелетон загрузки страницы-фильтра: ставится в момент клика (PrepareFilter),
    /// снимается по завершении LoadAsync. Поиск и фоновые пересборки (LibraryChanged,
    /// смена языка) его не трогают — чтобы скелетон не мигал при наборе и сканировании.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>«Пустая страница»: загрузка завершена и треков нет — иначе заглушка
    /// «библиотека пуста» мигала бы под скелетоном во время загрузки.</summary>
    public bool ShowEmptyState => !IsLoading && Tracks.Count == 0;

    /// <summary>Поколение поискового запроса: устаревшие debounce-прогоны отбрасываются.</summary>
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

        // VM живёт столько же, сколько приложение, поэтому отписка не нужна.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = PageTitle(_page);
            // Пересобираем список, чтобы DisplayArtist/DisplayAlbum перечитались.
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

        // Живая перезагрузка «Недавно прослушанные»: новая прослушка — трек встаёт
        // наверх, 50-й уходит из списка, без повторного захода на страницу.
        // Дебаунс 500 мс: прослушка пишется в момент старта трека рядом со сменой
        // карточек плеера — перезагрузка не должна соревноваться с этими кадрами.
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

    /// <summary>Синхронная часть смены фильтра — вызывается в момент клика, до анимации
    /// перехода: заголовок и скелетон появляются вместе со страницей. Сама загрузка
    /// (LoadAsync) откладывается до конца fade/slide (MainViewModel.LoadAfterTransitionAsync):
    /// Clear/Add сотен карточек посреди анимации ронял её кадры.</summary>
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
        // Карточки прежнего фильтра гасим СРАЗУ, в момент клика: страница библиотеки
        // одна на Home/Favorites/RecentlyPlayed, и без этого до завершения загрузки
        // под новым заголовком были видны карточки предыдущего бара (у сервисных
        // страниц такой проблемы нет — у них список живёт в своей VM и грузится
        // мгновенно из кэша карточек). Чистый лист → карточки, как у Яндекса.
        Tracks.Clear();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public void SetFilter(string page)
    {
        PrepareFilter(page);
        // Active search wins over the page filter.
        _ = LoadForCurrentSearchAsync();
    }

    /// <summary>Загрузка с учётом активного поиска (он перекрывает фильтр страницы):
    /// вызывается как отложенная загрузка после анимации перехода.</summary>
    public Task LoadForCurrentSearchAsync()
        => string.IsNullOrWhiteSpace(SearchText) ? LoadAsync() : ApplySearchAsync();

    public async Task LoadAsync()
    {
        // Локальные треки, лайки SoundCloud и музыка VK — независимые запросы: запускаем
        // все сразу и ждём вместе (Task.WhenAll) вместо последовательных await.
        // Ветка «Played» вместо локального List<Track> читает историю с played_at —
        // сливается с платформенными прослушками play_log по общему времени (см. Merge).
        Task<List<Track>>? localTask;
        Task<List<LibraryServiceLocalPlay>>? localDatedTask = null;
        switch (FilterMode)
        {
            case "Recent":    localTask = _library.GetRecentlyAddedAsync(); break;
            case "Played":    localTask = null; localDatedTask = _library.GetRecentlyPlayedDatedAsync(); break;
            case "Favorites": localTask = _library.GetFavoritesAsync(); break;
            default:          localTask = _library.GetAllTracksAsync(SortColumn, SortDirection); break;
        }
        // «Recent» (недавно добавленные) — только локальные: лайки не запрашиваем вовсе.
        var likesTask = FilterMode == "Recent"
            ? Task.FromResult<IReadOnlyList<SoundCloudLikeRow>>(new List<SoundCloudLikeRow>())
            : GetScLikesSafeAsync();
        // Музыка VK — «All» (дополнение списка) и «Played» (match прослушек play_log):
        // у VK в этой модели нет ни дат добавления («Recent»), ни лайков («Favorites»).
        var vkTask = FilterMode is "All" or "Played"
            ? GetVkRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<VkTrackRow>>(new List<VkTrackRow>());
        // Музыка Яндекс Музыки — аналогично VK, плюс «Favorites»: лайки ЯМ и есть
        // фавориты платформы (жалоба: добавленные в ЯМ треки не попадали в фавориты).
        var ymTask = FilterMode is "All" or "Played" or "Favorites"
            ? GetYmRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<YmTrackRow>>(new List<YmTrackRow>());
        // Лайки Spotify — только для match'а платформенных прослушек play_log на «Played».
        var isPlayed = string.Equals(FilterMode, "Played", StringComparison.Ordinal);
        var spotifyTask = isPlayed
            ? GetSpotifyRowsSafeAsync()
            : Task.FromResult<IReadOnlyList<SpotifyTrackRow>>(new List<SpotifyTrackRow>());
        // Платформенные прослушки (play_log, source != 'local') — только на «Played».
        var platformPlaysTask = isPlayed
            ? GetPlatformPlaysSafeAsync()
            : Task.FromResult<IReadOnlyList<LibraryServicePlatformPlay>>(new List<LibraryServicePlatformPlay>());

        var allTasks = new List<Task> { likesTask, vkTask, ymTask, spotifyTask, platformPlaysTask };
        if (localTask != null) allTasks.Add(localTask);
        if (localDatedTask != null) allTasks.Add(localDatedTask);
        await Task.WhenAll(allTasks);

        // Итоговый список собирается ПОЛНОСТЬЮ до Tracks.Clear(): построение карточек —
        // один синхронный проход без промежуточных await. Раньше SC-лайки досыпались
        // в Tracks уже после Clear/Add локальных, из-за чего список (и все карточки)
        // перестраивались дважды — заметный фриз при навигации по сайдбару.
        List<Track> full;
        if (localDatedTask != null)
        {
            // «Played»: локальная история + платформенные прослушки play_log,
            // честно отсортированные по времени прослушивания. Раньше платформенные
            // треки (track.Id < 0, в tracks их нет) в этот список не попадали вовсе,
            // а SC был представлен просто первыми 50 лайками — независимо от того,
            // играли их или нет; теперь список отражает фактические прослушки.
            full = MergeRecentlyPlayed(localDatedTask.Result, platformPlaysTask.Result,
                likesTask.Result, vkTask.Result, ymTask.Result, spotifyTask.Result);
        }
        else if (string.Equals(FilterMode, "Favorites", StringComparison.Ordinal))
        {
            // «Фавориты»: единый порядок «свежие сверху» по времени добавления —
            // лайки ЯМ/SC по времени лайка, локальные избранные по дате добавления
            // файла. Жалоба: свежелайкнутые треки «не добавлялись» — попадали в
            // конец/середину длинного списка ниже чужих платформ.
            var ym = YmRuntimeTracks.BuildYmAppend(FilterMode, ymTask.Result, 0);
            full = new List<Track>(ym);
            full.AddRange(localTask!.Result);
            full.AddRange(SoundCloudRuntimeTracks.BuildScAppend(FilterMode, likesTask.Result, ym.Count));
            full = SortByAddedDescending(full);
        }
        else
        {
            full = new List<Track>(localTask!.Result);
            // «Played» — 50 самых свежих лайков (liked_at DESC), «Favorites»/«All» (Home) —
            // все, «Recent» — ни одного (см. BuildScAppend).
            full.AddRange(SoundCloudRuntimeTracks.BuildScAppend(FilterMode, likesTask.Result));
            // VK — только на «All»; нумерация runtime-Id продолжается после SC-карточек,
            // чтобы отрицательные Id не пересекались в одном списке.
            full.AddRange(VkRuntimeTracks.BuildVkAppend(FilterMode, vkTask.Result, likesTask.Result.Count));
            // Яндекс Музыка — после VK; нумерация runtime-Id продолжается после SC+VK.
            full.AddRange(YmRuntimeTracks.BuildYmAppend(FilterMode, ymTask.Result,
                likesTask.Result.Count + vkTask.Result.Count));
            // Единый порядок «новые сверху» НЕЗАВИСИМО от источника: свежедобавленный трек
            // любой платформы встаёт над старыми треками остальных (раньше список был
            // жёсткими блоками local→SC→VK→YM, и новый трек поднимался только внутри
            // своего блока). Сортировка стабильна: при равных датах сохраняется прежний
            // блочный порядок, а внутри блока — свой порядок платформы
            // (date_added DESC / liked_at DESC / порядок каталога VK).
            if (string.Equals(FilterMode, "All", StringComparison.Ordinal))
                full = SortByAddedDescending(full);
        }

        SetTracks(full);
        IsLoading = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Лимит страницы «Недавно прослушанные» — как GetRecentlyPlayedAsync.</summary>
    private const int RecentlyPlayedLimit = 50;

    /// <summary>
    /// Слияние «Недавно прослушанные»: локальные треки (history) и платформенные прослушки
    /// (play_log, source != 'local') упорядочиваются по played_at по убыванию, берутся
    /// первые RecentlyPlayedLimit. Платформенные строки конвертируются в runtime-карточки
    /// через точный матч (Title, Artist) со своим репозиторием (у прослушки в БД только
    /// снимок метаданных); без совпадения строка пропускается. FilePath остаётся пустым —
    /// файл резолвится при воспроизведении через AudioService.FilePathResolver.
    /// </summary>
    private List<Track> MergeRecentlyPlayed(
        List<LibraryServiceLocalPlay> local,
        IReadOnlyList<LibraryServicePlatformPlay> platformPlays,
        IReadOnlyList<SoundCloudLikeRow> likes,
        IReadOnlyList<VkTrackRow> vkRows,
        IReadOnlyList<YmTrackRow> ymRows,
        IReadOnlyList<SpotifyTrackRow> spotifyRows)
    {
        // Индекс по (Title, Artist): Dictionary по кортежу сравнивает элементы
        // ординально — то самое «точное сравнение». При дублях в справочнике
        // побеждает первая строка (как FirstOrDefault).
        var likeByKey = IndexByTitleArtist(likes, l => (l.Title, l.Artist));
        var vkByKey = IndexByTitleArtist(vkRows, r => (r.Title, r.Artist));
        var ymByKey = IndexByTitleArtist(ymRows, r => (r.Title, r.Artist));
        var spotifyByKey = IndexByTitleArtist(spotifyRows, r => (r.Title, r.Artist));

        // Нумерация runtime-Id продолжается по всем платформенным карточкам списка,
        // чтобы отрицательные Id не пересекались (SpotifyRuntimeTracks пишет Id как есть,
        // поэтому ей передаётся готовое отрицательное значение).
        var runtimeIndex = 0;
        var platform = new List<(Track Track, string PlayedAt)>(platformPlays.Count);
        foreach (var p in platformPlays)
        {
            // Приоритет — сохранённый platform_id (трек узнаваем без справочника: так в
            // «Недавно прослушанные» попадают прослушки ВОЛНЫ, которых нет среди лайков).
            // Фолбэк для старых записей без id — точный матч (Title, Artist) по справочнику.
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

        // played_at — ISO 8601 UTC (DateTime.UtcNow.ToString("o")); неразбираемые
        // (напр. NULL у древних строк history) — в самое начало списка.
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
    /// Единый порядок объединённого списка «новые сверху» по дате добавления независимо
    /// от источника (Track.DateAdded: локальные — date_added, платформенные — время
    /// лайка/первой синхронизации, см. TrackTimestamps). LINQ-сортировка стабильна:
    /// равные даты сохраняют блочный порядок построения списка.
    /// </summary>
    private static List<Track> SortByAddedDescending(List<Track> tracks)
        => tracks.OrderByDescending(t => t.DateAdded).ToList();

    /// <summary>Лайки — дополнение к локальной библиотеке: их сбой не должен прятать локальные треки.
    /// Репозиторий лайков читается здесь ровно один раз за LoadAsync (свой единственный
    /// запрос есть и в ApplySearchAsync).</summary>
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

    /// <summary>VK-музыка — дополнение к локальной библиотеке: сбой не должен прятать
    /// локальные треки (и SC-лайки). Репозиторий читается один раз за LoadAsync.</summary>
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

    /// <summary>Музыка Яндекс Музыки — дополнение к локальной библиотеке: сбой не должен
    /// прятать локальные треки (и SC/VK). Репозиторий читается один раз за LoadAsync.</summary>
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

    /// <summary>Лайки Spotify (для match'а прослушек play_log на «Played»): сбой не должен
    /// прятать локальные треки и другие платформы.</summary>
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

    /// <summary>Платформенные прослушки из play_log: сбой не должен прятать локальную историю.</summary>
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

    /// <summary>Полный набор треков фильтра.</summary>
    private void SetTracks(IEnumerable<Track> tracks)
    {
        _allTracks = tracks.ToList();
        Tracks.Clear();
        foreach (var t in _allTracks) Tracks.Add(t);
        // Пустое состояние зависит и от количества: пересборка без загрузки
        // (смена языка) тоже должна его пересчитать.
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

    /// <summary>Перемешать список страницы; если играет трек из этого списка — очередь
    /// плеера перестраивается по новому порядку (платформенная очередь — карточки того
    /// же источника, локальная — проигрываемые файлы перемешанного списка).</summary>
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
        if (playingCard == null) return; // играет не из этого списка — очередь не трогаем

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
    /// Клик по кнопке Play на обложке: если этот трек сейчас играет — пауза/возобновление,
    /// иначе — обычный запуск трека.
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

    /// <summary>Текущий ли это трек. Для платформенных карточек (SC/VK) сравниваем ещё
    /// источник и ScId: Id у runtime-треков отрицательные и могут совпасть между
    /// разными списками (Home/страницы платформ).</summary>
    private bool IsCurrentTrack(Track track)
        => _audio.CurrentTrack is Track current
           && track.IsSameTrackAs(current);

    private async Task PlayTrackCoreAsync(Track? track)
    {
        if (track == null) return;
        if (track.IsPlatformTrack)
        {
            // Очередь = все карточки этой платформы текущего Home-списка в порядке
            // отображения: Previous/Next ходят по всему списку, файлы (локальный матч
            // или mp3 из кэша) резолвятся на переходе через AudioService.FilePathResolver.
            var platformQueue = _allTracks.Where(t => t.Source == track.Source).ToList();
            _audio.PlayTrack(track, platformQueue);
            return;
        }
        _audio.PlayTrack(track, PlayableContextQueue());
    }

    /// <summary>
    /// Контекстная очередь без SC-карточек с пустым FilePath: плеер не может открыть
    /// ещё не резолвнутый файл, переход Next на такую позицию ронял бы воспроизведение.
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
        // Платформенную карточку (SC/VK) без резолвнутого файла в очередь не добавляем:
        // плеер не сможет её открыть.
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

        // Лайк YM-карточки уходит в АККАУНТ Яндекс Музыки (как сердце в плеере):
        // локальный UPDATE по отрицательному runtime-Id строки не находит, флажок
        // менялся только в памяти и терялся при перезагрузке страницы.
        if (track.Source == Track.SourceYandex)
        {
            var target = !track.IsFavorite;
            if (!await _ym.SetTrackLikedAsync(track.ScId, target))
                return; // API не подтвердил — сердечко не переключаем
            track.IsFavorite = target;
            return;
        }

        // Прочие платформенные runtime-карточки (VK/SC/Spotify): лайков в этой модели нет.
        if (track.Id <= 0) return;

        track.IsFavorite = !track.IsFavorite;
        await _library.SetFavoriteAsync(track.Id, track.IsFavorite);
    }

    partial void OnSearchTextChanged(string value)
    {
        // Debounce 200ms + поколение запроса: при быстром наборе задержки
        // срабатывают по очереди, но выполняется только поиск последнего ввода —
        // результаты устаревших прогонов (и их чтения БД) отбрасываются.
        var generation = ++_searchGeneration;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            if (generation != _searchGeneration) return; // ввод уже изменился
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

        // Поиск перекрывает фильтр страницы и ищет ещё и по платформенным метаданным —
        // лайкам SoundCloud, музыке VK и Яндекс Музыки (title/artist): совпавшие —
        // runtime-карточки в конце списка, файл резолвится на клике. Каждый репозиторий
        // читается здесь один раз; сбой одной платформы не должен прятать локальные результаты.
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
        // VK-совпадения — после SC; нумерация runtime-Id продолжается после уже
        // построенных карточек, чтобы отрицательные Id не пересекались.
        filtered.AddRange(VkRuntimeTracks.FilterWithVk(filtered.Count, vkRows, SearchText));
        // Совпадения Яндекс Музыки — после VK (нумерация продолжается).
        filtered.AddRange(YmRuntimeTracks.FilterWithYm(filtered.Count, ymRows, SearchText));
        // Тот же единый порядок «новые сверху», что и на странице без поиска:
        // совпадения платформ встают по своей дате добавления, а не глыбами в конце.
        SetTracks(SortByAddedDescending(filtered));
    }
}
