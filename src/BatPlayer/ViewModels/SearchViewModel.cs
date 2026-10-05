using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly SearchService _search;
    private readonly AudioService _audio;

    public ObservableCollection<Track> Results { get; } = new();

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private bool _isActive;

    public SearchViewModel(SearchService search, AudioService audio)
    {
        _search = search;
        _audio = audio;
    }

    partial void OnQueryChanged(string value)
    {
        // Debounce 200ms
        _ = Task.Delay(200).ContinueWith(_ => RunSearchAsync(value));
    }

    private async Task RunSearchAsync(string q)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(q)) { ResultCount = 0; return; }

        var tracks = await _search.SearchTracksAsync(q);
        foreach (var t in tracks) Results.Add(t);
        ResultCount = tracks.Count;
    }

    [RelayCommand] private void Clear()
    {
        Query = string.Empty;
        Results.Clear();
        ResultCount = 0;
    }

    [RelayCommand]
    private void Play(Track track)
    {
        // Явный клик по результату поиска: SC-runtime-карточка, провалившая резолв
        // ранее в этой сессии (IsAvailable=false), пробуется снова — сеть/VPN могли
        // вернуться; неудача покажет чистую ошибку без перескоков (ResolveFailurePolicy).
        if (track != null && track.Source == Track.SourceSoundCloud && !track.IsAvailable)
            track.IsAvailable = true;
        _audio.PlayTrack(track, Results);
    }
}
