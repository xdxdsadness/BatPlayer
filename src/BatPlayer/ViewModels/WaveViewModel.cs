using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.ViewModels;

/// <summary>
/// Карточка трека волны (любого источника: Яндекс-кандидат или «реанимированный»
/// трек из своих библиотек VK/SC/локальных). Артибути ArtworkLocalPath и IsCurrent
/// поднимают PropertyChanged: обложки докачиваются фоном после генерации, а флаг
/// «сейчас играет» обновляется при каждом переключении трека плеером.
/// </summary>
public sealed class WaveCard : System.ComponentModel.INotifyPropertyChanged
{
    public required string Source { get; init; }
    /// <summary>Идентификатор платформы (ym_id / vk_id / sc_id / local:{id}); для local
    /// совпадает с Track.ScId runtime-трека.</summary>
    public required string PlatformId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    public bool Streamable { get; init; } = true;

    /// <summary>Совпавший трек локальной библиотеки (для local — сам трек): бейдж
    /// «есть локально» на карточке.</summary>
    public Track? LocalTrack { get; init; }

    public bool HasLocalMatch => LocalTrack != null;
    public bool IsPlayable => Streamable || HasLocalMatch;
    public double CardOpacity => IsPlayable ? 1.0 : 0.45;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Loc.Get("UnknownArtist")
        : Artist;

    private string? _artworkLocalPath;

    /// <summary>Путь обложки в локальном кэше; null — плейсхолдер.</summary>
    public string? ArtworkLocalPath
    {
        get => _artworkLocalPath;
        set
        {
            if (_artworkLocalPath == value) return;
            _artworkLocalPath = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ArtworkLocalPath)));
        }
    }

    private bool _isCurrent;

    /// <summary>Этот трек сейчас в плеере (любой источник, сверка Source+PlatformId).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Страница «Моя волна»: рекомендации по вкусу из уже добавленной музыки. Слева —
/// «пластинка»: текущий трек волны (крутится при игре), слева от него прошлый,
/// под ним следующий — оба приглушены. Справа — сетка всей волны: Яндекс-кандидаты
/// и треки своих библиотек (VK/SC/локальные), у каждого свой логотип источника.
///
/// Воспроизведение — runtime-треки соответствующего источника: резолв плеера
/// (грид или hero) играет волну как очередь: Next/Previous ходят по рекомендациям.
/// </summary>
public partial class WaveViewModel : PageViewModel, ISearchablePage
{
    private readonly RecommendationService _wave;
    private readonly YmService _ym;
    private readonly SoundCloudService _soundCloud;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly YmTracksRepository _ymTracks;

    /// <summary>Минимальный интервал между синками лайков ЯМ при автоматических
    /// перегенерациях (радио после конца очереди): не дёргать API каждый трек.</summary>
    private static readonly TimeSpan AutoResyncInterval = TimeSpan.FromMinutes(5);

    private List<Track> _localTracks = new();

    public ObservableCollection<WaveCard> Cards { get; } = new();

    // === Универсальный поиск (строка в шапке окна) ===
    // Последняя сгенерированная волна хранится целиком: Cards показывает либо всё,
    // либо отфильтрованное подмножество. Hero-зона и очередь плеера строятся по Cards.
    private List<WaveCard> _allCards = new();
    private string _searchQuery = string.Empty;

    /// <summary>Фильтр карточек волны по названию и исполнителю; пустой запрос — полный список.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildCards();
        UpdateCurrentFlags();
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

    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _counterText = string.Empty;

    // ===== Hero-зона «пластинки» (левая половина страницы) =====
    [ObservableProperty] private WaveCard? _heroCurrent;
    [ObservableProperty] private WaveCard? _heroPrev;
    [ObservableProperty] private WaveCard? _heroNext;

    /// <summary>Hero-трек реально играет плеер (а не просто выбран): крутит пластинку.</summary>
    [ObservableProperty] private bool _isHeroPlaying;

    /// <summary>Ошибки для тоста главного окна (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Подключён ли Яндекс Музыки — без него граф похожести недоступен.</summary>
    public bool IsConnected => _ym.HasToken;

    public bool ShowEmptyState => !IsLoading && !IsGenerating && Cards.Count == 0;

    public WaveViewModel(RecommendationService wave, YmService ym, SoundCloudService soundCloud,
                         LibraryService library, AudioService audio, YmTracksRepository ymTracks)
    {
        _wave = wave;
        _ym = ym;
        _soundCloud = soundCloud;
        _library = library;
        _audio = audio;
        _ymTracks = ymTracks;
        Title = Loc.Get("WaveTitle");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("WaveTitle");
            RefreshHeaderText();
        };

        // Плеер сам листает волну по окончании трека (очередь = волна): hero и подсветка
        // текущей карточки обязаны следовать за CurrentTrack, игра/пауза крутят пластинку.
        _audio.CurrentTrackChanged += (_, _) => OnPlaybackChanged();
        _audio.PlayStateChanged += (_, _) => OnPlaybackChanged();
        // Волна доиграла до конца — микс обновляется сам и продолжает играть (радио).
        _audio.QueueEnded += OnWaveQueueEnded;
    }

    /// <summary>Синхронизация hero/подсветки с плеером (события могут приходить из
    /// не-UI потока — уводим на диспетчер).</summary>
    private void OnPlaybackChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) UpdateCurrentFlags();
        else dispatcher.BeginInvoke(UpdateCurrentFlags);
    }

    /// <summary>Вызывается из MainViewModel.Navigate("Wave") после анимации перехода:
    /// первая генерация на сессии выполняется сразу, повторные входы показывают тот же
    /// список — обновление кнопкой (волна должна быть предсказуемой в пределах сессии).</summary>
    public async Task OnNavigatedAsync()
    {
        if (Cards.Count > 0 || IsGenerating) return;
        await RunRefreshAsync(forceSync: true); // первый микс сессии — по свежим лайкам
    }

    [RelayCommand]
    private Task RefreshAsync() => RunRefreshAsync(forceSync: true);

    /// <summary>Ядро генерации: true — волна заменена успешно (для авто-обновления,
    /// которое решает, запускать ли новый микс).</summary>
    private async Task<bool> RunRefreshAsync(bool forceSync = false)
    {
        if (IsGenerating) return false;
        if (!IsConnected)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("WaveNotConnected"));
            return false;
        }

        IsGenerating = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        Logger.Info("Wave: generation started");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await SyncYmLikesAsync(forceSync, cts.Token);
            _localTracks = await _library.GetAllTracksAsync();
            // Источники сидов — на потоке вызывающего (общее соединение); генерация — фон.
            var sources = await _wave.GatherSourcesAsync(cts.Token);
            var items = await _wave.GenerateWaveAsync(sources, cts.Token);
            Logger.Info($"Wave: generated {items.Count} candidates");

            var cards = await Task.Run(() => BuildCards(items), cts.Token);
            _allCards = cards;
            RebuildCards();
            UpdateCurrentFlags(); // hero появляется сразу; обложки докатятся фоном

            await DownloadArtworksAsync(cards, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Wave: generation timed out or cancelled");
            ErrorOccurred?.Invoke(this, Loc.Get("WaveFailed"));
        }
        catch (YmApiException ex)
        {
            Logger.Error($"Wave generation failed (HTTP {ex.HttpCode})");
            ErrorOccurred?.Invoke(this, YmApiException.IsSessionError(ex.HttpCode)
                ? Loc.Get("YmSessionExpired")
                : Loc.Get("WaveFailed"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave generation failed");
            ErrorOccurred?.Invoke(this, Loc.Get("WaveFailed"));
        }
        finally
        {
            IsGenerating = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
        return false;
    }

    /// <summary>Синхронизация лайков Яндекс Музыки перед генерацией: микс собирается из
    /// локальной БД (ym_tracks), и без синка «Обновить микс» не видит треки, добавленные
    /// в ЯМ после прошлого синка. Ручное обновление и первый микс сессии синкают всегда,
    /// авто-перезапуск радио — не чаще AutoResyncInterval. Сбой синка (сеть/токен) не
    /// роняет генерацию: микс соберётся по текущей БД, а ошибку сессии покажут сами
    /// запросы рекомендаций.</summary>
    private async Task SyncYmLikesAsync(bool force, CancellationToken ct)
    {
        if (!IsConnected) return;
        var last = _ym.GetLastSyncedUtc();
        if (!force && last != null && DateTime.UtcNow - last.Value < AutoResyncInterval) return;

        try
        {
            Logger.Info("Wave: syncing Yandex Music likes before generation");
            await _ym.SyncLikedTracksAsync(_ymTracks, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave: YM likes sync failed — generating from current DB");
        }
    }

    /// <summary>
    /// Волна доиграла до конца (очередь плеера исчерпана): микс обновляется сам,
    /// новый микс сразу играет — радио без действий пользователя. Кончившаяся
    /// очередь не волна — игнорируем; событие может прийти из аудио-потока —
    /// продолжаем на UI-потоке.
    /// </summary>
    private async void OnWaveQueueEnded(object? sender, Track? lastTrack)
    {
        try
        {
            if (lastTrack == null) return;
            if (!Cards.Any(c => c.Source == lastTrack.Source && c.PlatformId == lastTrack.ScId))
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => OnWaveQueueEnded(sender, lastTrack));
                return;
            }

            if (!await RunRefreshAsync()) return; // ошибка генерации уже показана
            if (Cards.Count > 0) await PlayCardAsync(Cards[0]);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave auto-refresh failed");
        }
    }

    private List<WaveCard> BuildCards(List<WaveItem> items)
    {
        var index = MatchHelper.BuildIndex(_localTracks);
        return items.Select(item =>
        {
            var localTrack = item.Source == Track.SourceLocal
                ? _localTracks.FirstOrDefault(t => item.PlatformId == $"local:{t.Id}")
                : MatchHelper.FindLocalMatch(index, item.Artist, item.Title);

            return new WaveCard
            {
                Source = item.Source,
                PlatformId = item.PlatformId,
                Title = item.Title,
                Artist = item.Artist,
                DurationMs = item.DurationMs,
                ArtworkUrl = YmJsonParser.BuildArtworkUrl(item.CoverUri) ?? string.Empty,
                Streamable = item.Available,
                LocalTrack = localTrack,
                ArtworkLocalPath = item.LocalPath // VK/SC: обложка уже в кэше
            };
        }).ToList();
    }

    /// <summary>Батч-закачка обложек кандидатов в общий artworks_cache (до 4 параллельных
    /// скачиваний): Яндекс — через YmArtworkCache, SoundCloud — через прокси-слой SC
    /// (i1.sndcdn.com напрямую недоступен). VK/локальные приходят с готовыми путями.</summary>
    private async Task DownloadArtworksAsync(List<WaveCard> cards, CancellationToken ct)
    {
        var pending = cards
            .Where(c => c.ArtworkLocalPath == null && c.ArtworkUrl.Length > 0
                        && (c.Source == Track.SourceYandex || c.Source == Track.SourceSoundCloud))
            .ToList();
        if (pending.Count == 0) return;

        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(pending.Select(async card =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = card.Source == Track.SourceYandex
                    ? await _ym.EnsureArtworkAsync(card.PlatformId, card.ArtworkUrl, ct)
                    : await _soundCloud.EnsureArtworkPathAsync(card.PlatformId, card.ArtworkUrl, ct);
                return (card, path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Wave artwork download failed ({card.Source}:{card.PlatformId})");
                return (card, null);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (card, path) in results)
        {
            if (path == null) continue;
            card.ArtworkLocalPath = path; // INPC карточки → LazyCover.Path перечитает
        }
    }

    private void RefreshHeaderText()
        => CounterText = $"{Cards.Count} {Loc.Get("WaveCounter")}";

    // ===================== Hero «пластинка» =====================

    /// <summary>Пересчёт hero-тройки и подсветки текущей карточки по состоянию плеера.
    /// Ничего не играет — hero показывает первую карточку волны (не крутится).</summary>
    private void UpdateCurrentFlags()
    {
        var current = _audio.CurrentTrack;
        foreach (var card in Cards)
            card.IsCurrent = current != null
                             && current.Source == card.Source
                             && current.ScId == card.PlatformId;

        var idx = current != null
            ? Cards.ToList().FindIndex(c => c.IsCurrent)
            : -1;

        if (idx < 0)
        {
            // Трек волны не играет: hero = первая карточка, но пластинка не крутится.
            // Боковые слоты выставляются ДО HeroCurrent: в обработчике PropertyChanged
            // (анимация перелёта во View) оба слота уже должны быть актуальны.
            HeroPrev = null;
            HeroNext = Cards.Count > 1 ? Cards[1] : null;
            HeroCurrent = Cards.Count > 0 ? Cards[0] : null;
            IsHeroPlaying = false;
            return;
        }

        HeroPrev = idx > 0 ? Cards[idx - 1] : null;
        HeroNext = idx + 1 < Cards.Count ? Cards[idx + 1] : null;
        HeroCurrent = Cards[idx];
        IsHeroPlaying = _audio.IsPlaying;
    }

    // ===================== Воспроизведение =====================

    /// <summary>Runtime-трек карточки по её источнику: Яндекс — как карточки страницы
    /// ЯМ, VK/SC — как карточки своих страниц, local — файл библиотеки (играет офлайн,
    /// без резолва). FilePath пуст у платформенных — резолвится плеером на переходах.</summary>
    private static Track BuildRuntimeTrack(WaveCard card, int index)
    {
        if (card.Source == Track.SourceYandex)
            return YmRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, card.Streamable, index);
        if (card.Source == Track.SourceVk)
            return VkRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, index);
        if (card.Source == Track.SourceSoundCloud)
            return SoundCloudRuntimeTracks.BuildRuntimeTrack(
                card.PlatformId, card.Title, card.Artist, card.DurationMs,
                card.ArtworkLocalPath, index);

        // local: файл известен — играется напрямую, резолв не нужен.
        return new Track
        {
            Id = -1 - index,
            FilePath = card.LocalTrack?.FilePath ?? string.Empty,
            ScId = card.PlatformId,
            Title = card.Title,
            Artist = card.Artist,
            DurationTicks = card.DurationMs * TimeSpan.TicksPerMillisecond,
            IsFavorite = card.LocalTrack?.IsFavorite ?? false,
            IsAvailable = true,
            Source = Track.SourceLocal
        };
    }

    /// <summary>
    /// Клик по карточке (hero, прошлый/следующий, сетка): играет волну как очередь.
    /// Повторный клик по играющей — пауза/возобновление. Анти-дубль клика — по образцу
    /// YmMusicViewModel (Play-кнопка и MouseLeftButtonUp за миллисекунды).
    /// </summary>
    private WaveCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(WaveCard? card)
    {
        if (card == null || IsGenerating) return Task.CompletedTask;
        if (!card.IsPlayable) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Повторный клик по играющей карточке волны — пауза/возобновление.
            if (_audio.CurrentTrack is Track current
                && current.Source == card.Source && current.ScId == card.PlatformId)
            {
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            var queue = Cards.Select((c, i) => BuildRuntimeTrack(c, i)).ToList();
            var start = queue[Cards.IndexOf(card)];
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Wave play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
