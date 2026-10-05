using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;

namespace BatPlayer.ViewModels;

/// <summary>
/// Карточка лайкнутого трека SoundCloud. Строится один раз при загрузке страницы и
/// после синка — поэтому вычисляемые свойства (IsPlayable и т.п.) не нуждаются в INPC.
/// </summary>
public sealed class SoundCloudCard
{
    public required string ScId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Имя исполнителя для отображения (общий ArtistHoverTemplate биндит
    /// именно его): пустое значение заменяется локализованным «Unknown artist».</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Путь обложки в локальном кэше (artworks_cache); null — ещё не скачана,
    /// карточка показывает плейсхолдер IconCloud. Биндим именно локальный путь: i1.sndcdn.com
    /// недоступен напрямую, а BitmapImage качает без прокси.</summary>
    public required string? ArtworkLocalPath { get; init; }

    public required string PermalinkUrl { get; init; }
    public required bool Streamable { get; init; }

    /// <summary>Совпавший трек локальной библиотеки (null — матча нет).</summary>
    public Track? LocalTrack { get; init; }

    /// <summary>Есть ли матч с локальной библиотекой (бейдж IconFile, офлайн-воспроизведение).</summary>
    public bool HasLocalMatch => LocalTrack != null;

    /// <summary>Можно ли воспроизвести: стрим доступен ИЛИ есть локальный матч.</summary>
    public bool IsPlayable => Streamable || HasLocalMatch;

    /// <summary>Недоступные треки приглушаются (по образцу недоступных локальных файлов).</summary>
    public double CardOpacity => IsPlayable ? 1.0 : 0.45;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// Страница «SoundCloud»: сетка лайкнутых треков из локальной БД (soundcloud_likes),
/// синхронизация с /me/likes/tracks, матчинг с локальной библиотекой, стриминг.
/// Онлайн-треки качаются в дисковый кэш (SoundCloudStreamCache) и играются локальным файлом.
/// </summary>
public partial class SoundCloudLikesViewModel : PageViewModel, ISearchablePage
{
    private readonly SoundCloudService _soundCloud;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SoundCloudLikesRepository _repository;

    // Локальная библиотека для матчинга; перечитывается при каждой загрузке страницы.
    private List<Track> _localTracks = new();

    // Авто-синк «при первом открытии» — выполняется один раз за жизнь приложения.
    private bool _autoSyncChecked;

    // Карточки уже прочитаны из БД: повторный вход на страницу
    // не пересобирает список (данные меняет только синк).
    private bool _cardsLoaded;

    /// <summary>Карта «путь кэш-файла обложки → карточка» для лоадера LazyCover:
    /// видимая карточка без скачанной обложки качает её по требованию (см. ctor).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SoundCloudCard> _cardsByArtworkPath = new();

    public ObservableCollection<SoundCloudCard> Cards { get; } = new();

    // === Универсальный поиск (строка в шапке окна) ===
    // Полный список карточек хранится отдельно: Cards показывает либо всё, либо
    // отфильтрованное подмножество; после синка/перезагрузки фильтр применяется заново.
    private List<SoundCloudCard> _allCards = new();
    private string _searchQuery = string.Empty;

    /// <summary>Фильтр карточек страницы по названию и исполнителю; пустой запрос — полный список.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildCards();
    }

    private void RebuildCards()
    {
        var q = _searchQuery.Trim();
        Cards.Clear();
        foreach (var card in _allCards)
            if (q.Length == 0
                || card.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || card.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                Cards.Add(card);
        RefreshHeaderText();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [ObservableProperty] private int _syncProgress;
    [ObservableProperty] private int _syncTotal;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _lastSyncedText = string.Empty;

    /// <summary>Идёт загрузка SC-трека в кэш: синк и клики по карточкам игнорируются.</summary>
    [ObservableProperty] private bool _isLoadingTrack;

    /// <summary>Идёт чтение карточек из БД: на это время показываются скелетоны.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Ошибки для тоста главного окна (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Подключён ли аккаунт по наличию sc_auth.json (без сетевой проверки).</summary>
    public bool IsConnected => _soundCloud.HasAuthFile;

    /// <summary>Показывать «пустую страницу»: загрузка закончена и карточек нет.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    /// <param name="streamCache">Не используется: файлы SC-карточек резолвит плеер
    /// (AudioService.FilePathResolver); параметр оставлен для совместимости вызовов.</param>
    public SoundCloudLikesViewModel(SoundCloudService soundCloud, LibraryService library,
                                    AudioService audio, SoundCloudLikesRepository repository,
                                    SoundCloudStreamCache? streamCache = null)
    {
        _soundCloud = soundCloud;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = Loc.Get("SoundCloud");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("SoundCloud");
            RefreshHeaderText();
        };

        _soundCloud.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };

        // Лоадер LazyCover: у реализовавшейся (видимой) карточки файл обложки ещё не
        // скачан → качаем его сейчас. Очередь LazyCover приоритетная, поэтому сначала
        // качаются обложки ближних карточек, при скролле — только видимые. Батч-синк
        // докачивает остальное фоном через тот же дедуп SoundCloudService.
        Controls.LazyCover.SetFileLoader(async path =>
        {
            if (!_cardsByArtworkPath.TryGetValue(path, out var card)) return false;
            if (string.IsNullOrEmpty(card.ArtworkUrl)) return false;
            var downloaded = await _soundCloud
                .EnsureArtworkPathAsync(card.ScId, card.ArtworkUrl, CancellationToken.None)
                .ConfigureAwait(false);
            return downloaded != null;
        });
    }

    /// <summary>
    /// Вызывается из MainViewModel.Navigate("SoundCloud"): перечитать карточки из БД;
    /// при первом открытии — авто-синк, если подключено и прошло &gt;30 минут.
    /// </summary>
    public async Task OnNavigatedAsync()
    {
        if (_cardsLoaded) return;

        await LoadCardsAsync();

        if (_autoSyncChecked) return;
        _autoSyncChecked = true;

        if (IsConnected && !IsSyncing)
        {
            var last = _soundCloud.GetLastSyncedUtc();
            if (last == null || DateTime.UtcNow - last.Value > TimeSpan.FromMinutes(30))
                await SyncNowAsync();
        }
    }

    private async Task LoadCardsAsync()
    {
        IsLoading = true;
        try
        {
            var localTask = _library.GetAllTracksAsync();
            var rowsTask = _repository.GetAllAsync();
            await Task.WhenAll(localTask, rowsTask);
            _localTracks = localTask.Result;
            var rows = rowsTask.Result;

            // Тяжёлая часть (индекс матчинга + сборка карточек по всей библиотеке) —
            // в фоне: на UI-потоке она держала компоновку, и переход на страницу лагал.
            var fresh = await Task.Run(() =>
            {
                // Матчинг через прединдекс: O(M) на индекс + O(1) на карточку
                // (раньше O(N*M) сравнений строк на каждый вход в бар).
                var index = MatchHelper.BuildIndex(_localTracks);

                // Батчевое обновление: панель реализует только окно, но Clear+Add по одному
                // даёт N уведомлений; при сотнях карточек заметно. Собираем в список.
                var list = new List<SoundCloudCard>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new SoundCloudCard
                    {
                        ScId = row.ScId,
                        Title = row.Title,
                        Artist = row.Artist,
                        DurationMs = row.DurationMs,
                        ArtworkUrl = row.ArtworkUrl,
                        ArtworkLocalPath = row.ArtworkLocalPath,
                        PermalinkUrl = row.PermalinkUrl,
                        Streamable = row.Streamable,
                        LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                    });
                }

                // Карта для лоадера LazyCover: по пути кэша он находит карточку и качает
                // ей обложку по требованию (файл может появиться и позже, от батч-синка —
                // тогда лоадер просто вернёт уже скачанный файл мгновенно).
                _cardsByArtworkPath.Clear();
                foreach (var card in list)
                    _cardsByArtworkPath[_soundCloud.GetArtworkCachePath(card.ScId)] = card;
                return list;
            });

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud likes load failed");
        }        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Перемешать карточки страницы; если играет SC-трек из этого списка —
    /// очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.IsPlayable
                ? SoundCloudRuntimeTracks.BuildRuntimeTrack(c.ScId, c.Title, c.Artist,
                    c.DurationMs, c.ArtworkLocalPath, i)
                : null,
            (playing, c) => playing.Source == Track.SourceSoundCloud && playing.ScId == c.ScId,
            "SoundCloud");
    }

    private void RefreshHeaderText()
    {
        var last = _soundCloud.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("SoundCloudNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        CounterText = $"{Cards.Count} {Loc.Get("SoundCloudLikedCounter")}";
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing || IsLoadingTrack) return;
        if (!IsConnected)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("NotConnected"));
            return;
        }

        IsSyncing = true;
        SyncProgress = 0;
        SyncTotal = 0;
        try
        {
            await _soundCloud.SyncLikesAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
        }
        catch (SoundCloudApiException ex)
        {
            Logger.Error($"SoundCloud sync failed: HTTP {ex.StatusCode}");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("SoundCloudSyncFailed")}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudSyncFailed"));
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Клик по карточке: играет её (файл резолвится плеером через FilePathResolver —
    /// локальный матч или mp3 из кэша/сети). Очередь = все играбельные карточки страницы
    /// (streamable или с локальным матчем), поэтому Previous/Next ходят по всему списку.
    /// Недоступные (не streamable, без матча) — тост объясняет.
    /// </summary>
    /// <summary>Анти-дубль клика: команда кнопки Play на обложке и всплывший до карточки
    /// MouseLeftButtonUp дёргают одну команду дважды за миллисекунды; второй вызов видел
    /// «трек уже играет» и ставил его на паузу — клик «не работал с первого раза».</summary>
    private SoundCloudCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(SoundCloudCard? card)
    {
        if (card == null || IsSyncing || IsLoadingTrack) return Task.CompletedTask;
        if (!card.IsPlayable)
        {
            // Нестримуемая и без локального матча — клик по дизайну пустой; молчание
            // выглядело как «плеер сломался», объясняем тостом.
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudUnavailable"));
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Повторный клик по играющей SC-карточке — пауза/возобновление. Но пока трек
            // ещё ОТКРЫВАЕТСЯ (ни Playing, ни Paused — идёт резолв потока), тоггл ломал
            // цепочку открытия и звук не появлялся с первого клика.
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceSoundCloud && current.ScId == card.ScId)
            {
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Runtime-карточки строятся на клик (не хранятся в карточках страницы):
            // FilePath пуст — резолвится на каждом переходе по очереди.
            var queue = Cards.Where(c => c.IsPlayable)
                             .Select((c, i) => SoundCloudRuntimeTracks.BuildRuntimeTrack(
                                 c.ScId, c.Title, c.Artist, c.DurationMs, c.ArtworkLocalPath, i))
                             .ToList();
            var start = queue.First(t => t.ScId == card.ScId);
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
