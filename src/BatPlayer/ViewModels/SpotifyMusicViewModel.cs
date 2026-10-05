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
using BatPlayer.Services.Spotify;

namespace BatPlayer.ViewModels;

/// <summary>
/// Карточка трека Spotify для UI (аналогично SoundCloudCard).
/// </summary>
public sealed class SpotifyCard
{
    public required string SpotifyId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required long DurationMs { get; init; }
    public required string ArtworkUrl { get; init; }
    /// <summary>Имя исполнителя для отображения (общий ArtistHoverTemplate биндит
    /// именно его): пустое значение заменяется локализованным «Unknown artist».</summary>
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;
    public required string? ArtworkLocalPath { get; init; }
    public required bool IsPlayable { get; init; }

    /// <summary>Совпавший трек локальной библиотеки (null — матча нет).</summary>
    public Track? LocalTrack { get; init; }

    /// <summary>Есть ли матч с локальной библиотекой.</summary>
    public bool HasLocalMatch => LocalTrack != null;

    /// <summary>Можно ли воспроизвести: только если есть локальный матч.</summary>
    public bool CanPlay => HasLocalMatch;

    /// <summary>Недоступные треки приглушаются.</summary>
    public double CardOpacity => CanPlay ? 1.0 : 0.45;


    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// Страница «Spotify»: сетка Saved Tracks (Liked Songs) из локальной БД,
/// синхронизация через официальный Spotify Web API, матчинг с локальной библиотекой.
/// 
/// Spotify API не позволяет стриминг MP3 — воспроизведение только через локальные матчи.
/// </summary>
public partial class SpotifyMusicViewModel : PageViewModel, ISearchablePage
{
    private readonly SpotifyService _spotify;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SpotifyTracksRepository _repository;

    // Локальная библиотека для матчинга
    private List<Track> _localTracks = new();

    // Авто-синк при первом открытии
    private bool _autoSyncChecked;

    // Карточки уже прочитаны из БД: повторный вход на страницу
    // не пересобирает список (данные меняет только синк).
    private bool _cardsLoaded;

    public ObservableCollection<SpotifyCard> Cards { get; } = new();

    // === Универсальный поиск (строка в шапке окна) ===
    // Полный список карточек хранится отдельно: Cards показывает либо всё, либо
    // отфильтрованное подмножество; после синка/перезагрузки фильтр применяется заново.
    private List<SpotifyCard> _allCards = new();
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
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _lastSyncedText = string.Empty;

    /// <summary>Ошибки для тоста главного окна.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Подключён ли аккаунт Spotify.</summary>
    public bool IsConnected => _spotify.HasAuthFile;

    /// <summary>Показывать «пустую страницу»: загрузка закончена и карточек нет.</summary>
    public bool ShowEmptyState => !IsLoading && Cards.Count == 0;

    public SpotifyMusicViewModel(SpotifyService spotify, LibraryService library,
                                 AudioService audio, SpotifyTracksRepository repository)
    {
        _spotify = spotify;
        _library = library;
        _audio = audio;
        _repository = repository;
        Title = "Spotify";

        Loc.LanguageChanged += (_, _) =>
        {
            Title = "Spotify";
            RefreshHeaderText();
        };

        _spotify.SyncProgress += (_, p) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SyncProgress = p.done;
                SyncTotal = p.total;
            });
        };
    }

    /// <summary>
    /// Вызывается при переходе на страницу: перечитать карточки из БД;
    /// при первом открытии — авто-синк, если подключено и прошло >30 минут.
    /// </summary>
    public async Task OnNavigatedAsync()
    {
        if (_cardsLoaded) return;

        await LoadCardsAsync();

        if (_autoSyncChecked) return;
        _autoSyncChecked = true;

        if (IsConnected && !IsSyncing)
        {
            var last = _spotify.GetLastSyncedUtc();
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

            // Матчинг через прединдекс
            var index = MatchHelper.BuildIndex(_localTracks);

            var fresh = new List<SpotifyCard>(rows.Count);
            foreach (var row in rows)
            {
                fresh.Add(new SpotifyCard
                {
                    SpotifyId = row.SpotifyId,
                    Title = row.Title,
                    Artist = row.Artist,
                    Album = row.Album,
                    DurationMs = row.DurationMs,
                    ArtworkUrl = row.ArtworkUrl,
                    ArtworkLocalPath = row.ArtworkLocalPath,
                    IsPlayable = row.IsPlayable,
                    LocalTrack = MatchHelper.FindLocalMatch(index, row.Artist, row.Title)
                });
            }

            _allCards = fresh;
            RebuildCards();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify tracks load failed");
        }
        finally
        {
            _cardsLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Перемешать карточки страницы; если играет трек этого списка (играются
    /// локальные матчи) — очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShuffleCards()
    {
        if (IsSyncing || IsImporting) return;
        Helpers.CardsShuffler.Shuffle(Cards, _audio,
            (c, i) => c.CanPlay && c.LocalTrack != null ? c.LocalTrack : null,
            (playing, c) => playing.SpotifyId == c.SpotifyId,
            "Spotify");
    }

    private void RefreshHeaderText()
    {
        var last = _spotify.GetLastSyncedUtc();
        LastSyncedText = last == null
            ? Loc.Get("SoundCloudNever")
            : last.Value.ToLocalTime().ToString("g", Loc.CurrentCulture);
        
        var matched = Cards.Count(c => c.HasLocalMatch);
        CounterText = $"{Cards.Count} tracks ({matched} matched)";
    }

    /// <summary>
    /// Открыть окно входа в Spotify.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsSyncing || IsImporting) return;

        var loginWindow = new Views.SpotifyLoginWindow(_spotify) 
        { 
            Owner = System.Windows.Application.Current.MainWindow 
        };

        var result = loginWindow.ShowDialog();
        if (result == true)
        {
            Logger.Info("Spotify connected successfully");
            OnPropertyChanged(nameof(IsConnected));
            await SyncNowAsync();
        }
    }

    /// <summary>
    /// Отключить аккаунт Spotify (удалить токены).
    /// </summary>
    [RelayCommand]
    private void Disconnect()
    {
        if (IsSyncing || IsImporting) return;

        var result = System.Windows.MessageBox.Show(
            "Disconnect from Spotify and delete all saved tracks?",
            "Spotify",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _spotify.Disconnect();
            _allCards = new List<SpotifyCard>();
            RebuildCards();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ShowEmptyState));
            Logger.Info("Spotify disconnected");
        }
    }

    /// <summary>
    /// Синхронизация Saved Tracks с Spotify.
    /// </summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing || IsImporting) return;
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
            var count = await _spotify.SyncSavedTracksAsync(_repository, CancellationToken.None);
            await LoadCardsAsync();
            Logger.Info($"Spotify sync completed: {count} tracks");
        }
        catch (SpotifyApiException ex)
        {
            Logger.Error($"Spotify sync failed: HTTP {ex.StatusCode}");
            ErrorOccurred?.Invoke(this, $"Spotify sync failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify sync failed");
            ErrorOccurred?.Invoke(this, "Spotify sync failed");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Бесплатный импорт библиотеки без Web API (Development Mode Spotify с
    /// февраля 2026 требует Premium): официальный экспорт «Download your data»
    /// (ZIP/JSON с YourLibrary.json / Playlist*.json) или CSV экспортёров.
    /// Результат — в тот же spotify_tracks, что и синк Web API.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        if (IsSyncing || IsImporting) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Spotify library",
            Filter = "Spotify data export (*.zip;*.json)|*.zip;*.json|CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        IsImporting = true;
        try
        {
            var rows = await Task.Run(() => SpotifyImportService.ParseFile(dialog.FileName));
            if (rows.Count == 0)
            {
                ErrorOccurred?.Invoke(this, Loc.Get("SpotifyImportNoTracks"));
                return;
            }

            await _repository.UpsertBatchAsync(rows);
            _spotify.SetLastSyncedUtc(DateTime.UtcNow);
            await LoadCardsAsync();

            Logger.Info($"Spotify import completed: {rows.Count} tracks");
            ErrorOccurred?.Invoke(this, string.Format(Loc.CurrentCulture,
                Loc.Get("SpotifyImportDone"), rows.Count));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify import failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("SpotifyImportFailed")}: {ex.Message}");
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Воспроизведение трека Spotify (только через локальный матч).
    /// </summary>
    /// <summary>Анти-дубль клика: команда кнопки Play на обложке и всплывший до карточки
    /// MouseLeftButtonUp дёргают одну команду дважды за миллисекунды; второй вызов видел
    /// «трек уже играет» и ставил его на паузу — клик «не работал с первого раза».</summary>
    private SpotifyCard? _lastClickedCard;
    private DateTime _lastClickTime;

    [RelayCommand]
    private Task PlayCardAsync(SpotifyCard? card)
    {
        if (card == null || IsSyncing) return Task.CompletedTask;
        if (!card.CanPlay) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClickedCard, card) && (now - _lastClickTime).TotalMilliseconds < 300)
            return Task.CompletedTask;
        _lastClickedCard = card;
        _lastClickTime = now;

        try
        {
            // Повторный клик — пауза/возобновление
            if (_audio.CurrentTrack is Track current
                && current.Source == Track.SourceSpotify && current.SpotifyId == card.SpotifyId)
            {
                // Тоггл только при живом воспроизведении: пока трек ещё открывается
                // (резолв потока), PlayPauseToggle ломал цепочку открытия.
                if (_audio.IsPlaying || _audio.IsPaused)
                    _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }

            // Воспроизводим локальный матч
            if (card.LocalTrack != null)
            {
                var queue = Cards.Where(c => c.CanPlay && c.LocalTrack != null)
                                 .Select(c => c.LocalTrack!)
                                 .ToList();
                _audio.PlayTrack(card.LocalTrack, queue);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify play failed");
            ErrorOccurred?.Invoke(this, $"Playback error: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
