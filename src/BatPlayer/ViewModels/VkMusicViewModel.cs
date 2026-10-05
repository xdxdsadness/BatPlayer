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
using BatPlayer.Services.Vk;

namespace BatPlayer.ViewModels;

/// <summary>
/// Карточка трека VK Music. Строится один раз при загрузке страницы и после синка —
/// поэтому вычисляемые свойства (IsPlayable и т.п.) не нужны в INPC.
/// </summary>
public sealed class VkCard
{
    public required string VkId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Имя исполнителя для отображения (общий ArtistHoverTemplate биндит
    /// именно его): пустое значение заменяется локализованным «Unknown artist».</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Путь обложки в локальном кэше (artworks_cache/vk_{vk_id}.jpg); null — ещё
    /// не скачана, карточка показывает плейсхолдер IconVk.</summary>
    public required string? ArtworkLocalPath { get; init; }

    /// <summary>Трек доступен для стриминга. У VK нет флага streamable как у SoundCloud:
    /// всё, что пришло из каталога al_audio, воспроизводимо (ссылка разрешается на клике).</summary>
    public bool Streamable { get; init; } = true;

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
/// Страница «VK Music»: сетка треков из локальной БД (vk_tracks), синхронизация через
/// веб-эндпоинт al_audio.php (cookies веб-сессии), матчинг с локальной библиотекой,
/// стриминг. Онлайн-треки качаются в дисковый кэш (VkStreamCache) и играются локальным
/// файлом; скачивание файлов пользователю не предоставляется — только кэш для воспроизведения.
/// </summary>
public partial class VkMusicViewModel : PageViewModel, ISearchablePage
{
    private readonly VkService _vk;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly VkTracksRepository _repository;

    // Локальная библиотека для матчинга; перечитывается при каждой загрузке страницы.
    private List<Track> _localTracks = new();

    // Авто-синк «при первом открытии» — выполняется один раз за жизнь приложения.
    private bool _autoSyncChecked;

    // Карточки уже прочитаны из БД: повторный вход на страницу
    // не пересобирает список (данные меняет только синк).
    private bool _cardsLoaded;

    public ObservableCollection<VkCard> Cards { get; } = new();

    // === Универсальный поиск (строка в шапке окна) ===
    // Полный список карточек хранится отдельно: Cards показывает либо всё, либо
    // отфильтрованное подмножество; после синка/перезагрузки фильтр применяется заново.
    private List<VkCard> _allCards = new();
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

    /// <summary>Идёт загрузка VK-трека в кэш: синк и клики по карточкам игнорируются.</summary>
    [ObservableProperty] private bool _isLoadingTrack;

    /// <summary>Идёт чтение карточек из БД: на это время показываются скелетоны.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Ошибки для тоста главного окна (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Подключён ли аккаунт по наличию cookies веб-сессии (без сетевой проверки).</summary>
    public bool IsConnected => _vk.HasWebSession;

    /// <summary>Показывать «пустую страницу»: загрузка закончена и карточек нет.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    public VkMusicViewModel(VkService vk, LibraryService library, AudioService audio,
                            VkTracksRepository repository)
    {
        _vk = vk;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = Loc.Get("VkMusic");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("VkMusic");
            RefreshHeaderText();
        };

        _vk.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };
    }

    /// <summary>
    /// Вызывается из MainViewModel.Navigate("VkMusic"): перечитать карточки из БД;
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
            var last = _vk.GetLastSyncedUtc();
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

            // Матчинг через прединдекс: O(M) на индекс + O(1) на карточку.
            // Тяжёлая часть (индекс матчинга + сборка карточек по всей библиотеке) —
            // в фоне: на UI-потоке она держала компоновку, и переход на страницу лагал.
            var fresh = await Task.Run(() =>
            {
                var index = MatchHelper.BuildIndex(_localTracks);

                var list = new List<VkCard>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new VkCard
                {
                    VkId = row.VkId,
                    Title = row.Title,
                    Artist = row.Artist,
                    DurationMs = row.DurationMs,
                    ArtworkUrl = row.ArtworkUrl,
                    ArtworkLocalPath = row.ArtworkLocalPath,
                    LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                });
            }

                return list;
            });

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK music load failed");
        }        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Перемешать карточки страницы; если играет VK-трек из этого списка —
    /// очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing || IsLoadingTrack) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.IsPlayable
                ? VkRuntimeTracks.BuildRuntimeTrack(c.VkId, c.Title, c.Artist, c.DurationMs,
                    c.ArtworkLocalPath, i)
                : null,
            (playing, c) => playing.Source == Track.SourceVk && playing.ScId == c.VkId,
            "VK Music");
    }

    private void RefreshHeaderText()
    {
        var last = _vk.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("VkNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        CounterText = $"{Cards.Count} {Loc.Get("VkCounter")}";
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
            await _vk.SyncAudioAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
        }
        catch (VkApiException ex)
        {
            Logger.Error($"VK sync failed (error code {ex.ErrorCode})");
            ErrorOccurred?.Invoke(this, DescribeSyncError(ex));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("VkSyncFailed"));
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Понятное сообщение по коду ошибки VK: 5 — cookies протухли (сессия сброшена
    /// сервисом), 15/26/201 — нет доступа к аудио, остальное — общий текст синка.
    /// </summary>
    private static string DescribeSyncError(VkApiException ex)
    {
        if (ex.ErrorCode == 5) return Loc.Get("VkSessionExpired");
        if (VkService.IsAudioPermissionError(ex.ErrorCode)) return Loc.Get("VkAudioPermissionDenied");
        return $"{Loc.Get("VkSyncFailed")}: {ex.Message}";
    }

    /// <summary>
    /// Клик по карточке: играет её (файл резолвится плеером через FilePathResolver —
    /// локальный матч или mp3 из кэша/сети). Очередь = все играбельные карточки страницы,
    /// поэтому Previous/Next ходят по всему списку. Недоступные — ничего.
    /// </summary>
    /// <summary>Анти-дубль клика: команда кнопки Play на обложке и всплывший до карточки
    /// MouseLeftButtonUp дёргают одну команду дважды за миллисекунды; второй вызов видел
    /// «трек уже играет» и ставил его на паузу — клик «не работал с первого раза».</summary>
    private VkCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(VkCard? card)
    {
        if (card == null || IsSyncing || IsLoadingTrack) return Task.CompletedTask;
        if (!card.IsPlayable) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Повторный клик по играющей VK-карточке — пауза/возобновление.
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceVk && current.ScId == card.VkId)
            {
                // Тоггл только при живом воспроизведении: пока трек ещё открывается
                // (резолв потока), PlayPauseToggle ломал цепочку открытия.
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Runtime-карточки строятся на клик (не хранятся в карточках страницы):
            // FilePath пуст — резолвится на каждом переходе по очереди.
            var queue = Cards.Where(c => c.IsPlayable)
                             .Select((c, i) => VkRuntimeTracks.BuildRuntimeTrack(
                                 c.VkId, c.Title, c.Artist, c.DurationMs, c.ArtworkLocalPath, i))
                             .ToList();
            var start = queue.First(t => t.ScId == card.VkId);
            _audio.PlayTrack(start, queue);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
