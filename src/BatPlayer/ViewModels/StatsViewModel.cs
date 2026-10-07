using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Localization;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>Top-artists row.</summary>
public sealed class StatsArtistRow
{
    public required int Index { get; init; }
    public required string Artist { get; init; }
    public required int Plays { get; init; }
}

/// <summary>Top-tracks row (and "most replayed").</summary>
public sealed class StatsTrackRow
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required int Plays { get; init; }
}

/// <summary>
/// Statistics page: how many distinct tracks and plays over a period
/// (day/week/month/year/all), total hours of music, top-5 artists, top-10
/// tracks, and the track most often replayed. Data comes from play_log
/// (each play is its own row, including platform tracks).
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
    /// Load counter: rapid period clicks must not produce a race in which
    /// a slow stale response overwrites a fresh one.
    /// </summary>
    private int _loadSeq;

    public StatsViewModel(HistoryService history)
    {
        _history = history;
        Title = Loc.Get("StatsTitle");

        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("StatsTitle");
            _ = LoadAsync(); // re-read labels/counter in the new language
        };
    }

    /// <summary>Called from MainViewModel.Navigate("Statistics").</summary>
    public async Task OnNavigatedAsync() => await LoadAsync();

    /// <summary>Reload on ANY period change. Previously only the command loaded: a radio
    /// button click first set Period synchronously via two-way IsChecked binding, so the
    /// command saw "same period" and returned without re-reading the data — the stats
    /// never switched. Now this is the single load point.</summary>
    partial void OnPeriodChanged(StatsPeriod value) => _ = LoadAsync();

    [RelayCommand]
    private Task SetPeriodAsync(string period)
    {
        // Just parse and set the period — OnPeriodChanged does the loading.
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

            // A different period was chosen while loading: the response is stale — do not apply.
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
