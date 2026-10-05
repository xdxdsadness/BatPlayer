using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>
/// Страница «Загрузки»: локальные треки библиотеки — файлы, добавленные с компьютера
/// (Source=local), отсортированные по дате добавления (новые сверху). Скачанные SC-треки
/// остаются на вкладке SoundCloud (лайки). Клик играет трек; очередь — все локальные
/// треки страницы, поэтому Previous/Next ходят по всему списку.
/// </summary>
public partial class DownloadsViewModel : PageViewModel, ISearchablePage
{
    private readonly LibraryService _library;
    private readonly AudioService _audio;

    public ObservableCollection<Track> Tracks { get; } = new();

    // === Универсальный поиск (строка в шапке окна) ===
    private List<Track> _allTracks = new();
    private string _searchQuery = string.Empty;

    /// <summary>Фильтр списка по названию и исполнителю; пустой запрос — полный список.</summary>
    public void ApplySearch(string? query)
    {
        _searchQuery = query ?? string.Empty;
        RebuildTracks();
    }

    private void RebuildTracks()
    {
        var q = _searchQuery.Trim();
        Tracks.Clear();
        foreach (var t in _allTracks)
            if (q.Length == 0
                || t.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || t.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                Tracks.Add(t);
        RefreshTexts();
    }

    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _hintText = string.Empty;

    /// <summary>Ошибки для тоста главного окна (MainViewModel.ErrorMessage).</summary>
    public event EventHandler<string>? ErrorOccurred;

    public DownloadsViewModel(LibraryService library, AudioService audio)
    {
        _library = library;
        _audio = audio;
        Title = Loc.Get("Downloads");

        // VM живёт столько же, сколько приложение, поэтому отписка не нужна.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Downloads");
            RefreshTexts();
        };
    }

    /// <summary>Вызывается из MainViewModel.Navigate("Downloads"): пересобрать список.</summary>
    public async Task OnNavigatedAsync()
    {
        // Карточки прежнего содержимого гасим сразу, до запроса: иначе до завершения
        // загрузки под шапкой висели карточки прошлого визита. IsLoading прячет
        // пустое состояние, чтобы оно не мигало на время очистки.
        IsLoading = true;
        Tracks.Clear();
        RefreshTexts();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Локальные треки из БД (Source=local): недавно добавленные сверху.
            _allTracks = await _library.GetAllTracksAsync(SortColumn.DateAdded, SortDirection.Descending);
            RebuildTracks();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Downloads list load failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>«Пустая страница»: загрузка завершена и треков нет — иначе заглушка
    /// мигала бы на время очистки/загрузки списка.</summary>
    public bool ShowEmptyState => !IsLoading && Tracks.Count == 0;

    /// <summary>Перемешать список загрузок; если играет трек из этого списка —
    /// очередь плеера перестраивается по новому порядку.</summary>
    [RelayCommand]
    private void ShuffleTracks() =>
        Helpers.CardsShuffler.Shuffle(Tracks, _audio,
            (t, i) => t, (playing, t) => playing.Id == t.Id && playing.Source == t.Source, "Downloads");

    private void RefreshTexts()
    {
        CounterText = string.Format(Loc.Get("TracksCountFormat"), Tracks.Count);
        HintText = Loc.Get("DownloadsHint");
    }

    /// <summary>
    /// Клик по карточке: играет трек (SC-карточки на страницу не попадают — FilePath
    /// у локальных всегда задан); повторный клик по играющему — пауза/возобновление.
    /// Очередь = локальные треки страницы.
    /// </summary>
    [RelayCommand]
    private Task PlayPauseTrack(Track? track)
    {
        if (track == null || string.IsNullOrEmpty(track.FilePath)) return Task.CompletedTask;
        try
        {
            if (_audio.CurrentTrack is Track current && track.IsSameTrackAs(current))
            {
                _audio.PlayPauseToggle();
                return Task.CompletedTask;
            }
            _audio.PlayTrack(track, Tracks);
        }
        catch (Exception ex)
        {
            // Файл могли удалить между построением списка и кликом.
            Logger.Error(ex, "Downloads play failed");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("ErrorPlayback")}: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
