using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Localization;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>Строка топа исполнителей.</summary>
public sealed class StatsArtistRow
{
    public required int Index { get; init; }
    public required string Artist { get; init; }
    public required int Plays { get; init; }
}

/// <summary>Строка топа треков (и «чаще на повторе»).</summary>
public sealed class StatsTrackRow
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required int Plays { get; init; }
}

/// <summary>
/// Страница «Статистика»: сколько разных треков и сколько прослушиваний за период
/// (день/неделя/месяц/год/всё), суммарные часы музыки, топ-5 исполнителей, топ-10
/// треков и трек, который чаще всего ставили на повтор. Данные — из play_log
/// (каждая прослушка отдельной строкой, включая платформенные треки).
/// </summary>
public partial class StatsViewModel : PageViewModel
{
    private readonly HistoryService _history;

    public ObservableCollection<StatsArtistRow> TopArtists { get; } = new();
    public ObservableCollection<StatsTrackRow> TopTracks { get; } = new();

    [ObservableProperty] private string _counterText = string.Empty;
    [ObservableProperty] private string _uniqueTracksText = "0";
    [ObservableProperty] private string _playsText = "0";
    [ObservableProperty] private string _hoursText = "0";
    [ObservableProperty] private string _mostReplayedText = string.Empty;
    [ObservableProperty] private bool _hasMostReplayed;
    [ObservableProperty] private StatsPeriod _period = StatsPeriod.Week;

    /// <summary>
    /// Счётчик загрузок: быстрые клики по периодам не должны давать «гонку»,
    /// в которой медленный устаревший ответ перезаписывает свежий.
    /// </summary>
    private int _loadSeq;

    public StatsViewModel(HistoryService history)
    {
        _history = history;
        Title = Loc.Get("StatsTitle");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("StatsTitle");
            _ = LoadAsync(); // перечитать подписи/счётчик на новом языке
        };
    }

    /// <summary>Вызывается из MainViewModel.Navigate("Statistics").</summary>
    public async Task OnNavigatedAsync() => await LoadAsync();

    /// <summary>Перезагрузка на ЛЮБОЕ изменение периода.
    /// Раньше грузила только команда: клик по радиокнопке сначала синхронно
    /// выставлял Period через двусторонний IsChecked-биндинг, и команда видела
    /// «период уже тот» и выходила, не перечитав данные — статистика не
    /// переключалась. Теперь единственная точка загрузки — здесь.</summary>
    partial void OnPeriodChanged(StatsPeriod value) => _ = LoadAsync();

    [RelayCommand]
    private Task SetPeriodAsync(string period)
    {
        // Только парсим и выставляем период — загрузку делает OnPeriodChanged.
        if (Enum.TryParse<StatsPeriod>(period, ignoreCase: true, out var p))
            Period = p;
        return Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        var seq = ++_loadSeq;
        var period = Period;
        try
        {
            var stats = await _history.GetStatsAsync(period);

            // Пока грузились, выбрали другой период: ответ устарел — не применяем.
            if (seq != _loadSeq) return;

            UniqueTracksText = stats.UniqueTracks.ToString("N0");
            PlaysText = stats.Plays.ToString("N0");
            HoursText = stats.Hours.ToString("0.#");

            TopArtists.Clear();
            for (var i = 0; i < stats.TopArtists.Count; i++)
                TopArtists.Add(new StatsArtistRow
                {
                    Index = i + 1,
                    Artist = stats.TopArtists[i].Artist,
                    Plays = stats.TopArtists[i].Plays
                });

            TopTracks.Clear();
            for (var i = 0; i < stats.TopTracks.Count; i++)
                TopTracks.Add(new StatsTrackRow
                {
                    Index = i + 1,
                    Title = stats.TopTracks[i].Title,
                    Artist = stats.TopTracks[i].Artist,
                    Plays = stats.TopTracks[i].Plays
                });

            HasMostReplayed = stats.MostReplayed != null;
            MostReplayedText = stats.MostReplayed != null
                ? $"{stats.MostReplayed.Value.Title} — {stats.MostReplayed.Value.Artist} ({stats.MostReplayed.Value.Plays})"
                : Loc.Get("StatsNone");

            CounterText = $"{stats.Plays:N0} {Loc.Get("StatsPlays")} · {PeriodLabel(period)}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Stats load failed");
        }
    }

    private static string PeriodLabel(StatsPeriod period) => period switch
    {
        StatsPeriod.Day => Loc.Get("StatsDay"),
        StatsPeriod.Week => Loc.Get("StatsWeek"),
        StatsPeriod.Month => Loc.Get("StatsMonth"),
        StatsPeriod.Year => Loc.Get("StatsYear"),
        _ => Loc.Get("StatsAll")
    };
}
