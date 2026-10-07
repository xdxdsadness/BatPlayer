using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.ViewModels;

/// <summary>
/// Equalizer with a dynamic set of bands (in the spirit of FabFilter Pro-Q):
/// there are no bands at start — a flat line; the user adds points by double-clicking
/// the chart, removes them by double-clicking a point, and sets frequency/gain by dragging.
/// Every change is pushed to the audio service immediately.
/// </summary>
public partial class EqualizerViewModel : PageViewModel
{
    private const int MaxBands = 16;

    private readonly EqualizerService _service;
    private readonly AudioService _audio;
    private readonly SettingsService _settings;

    public ObservableCollection<EqualizerBand> Bands { get; } = new();
    public ObservableCollection<EqualizerPreset> Presets { get; } = new();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private EqualizerPreset? _selectedPreset;
    [ObservableProperty] private double _preGain;

    public EqualizerViewModel(EqualizerService service, AudioService audio, SettingsService settings)
    {
        _service = service;
        _audio = audio;
        _settings = settings;
        Title = Loc.Get("Equalizer");

        // VM lives as long as the app — no unsubscribe needed.
        Loc.LanguageChanged += (_, _) => Title = Loc.Get("Equalizer");

        RefreshPresets();
        LoadFromSettings();
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var p in _service.BuiltInPresets) Presets.Add(p);
        foreach (var p in _service.UserPresets)    Presets.Add(p);
    }

    private void LoadFromSettings()
    {
        // Bands are NOT restored at startup: per the Pro-Q mechanic the user starts
        // from a flat line and places points themselves; a preset applies only
        // when selected in the list.
        IsEnabled = _settings.Current.EqualizerEnabled;
        PreGain = 0;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _audio.SetEqualizerEnabled(value);
        _settings.Update(s => s.EqualizerEnabled = value);
    }

    partial void OnSelectedPresetChanged(EqualizerPreset? value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
        if (value != null)
        {
            ApplyPreset(value);
            _settings.Update(s => s.CurrentEqualizerPreset = value.Name);
        }
    }

    /// <summary>Only user presets can be deleted; built-ins are forever.</summary>
    public bool CanDeleteSelectedPreset => SelectedPreset is { IsBuiltIn: false };

    [RelayCommand]
    private void DeleteSelectedPreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset) return;
        _service.DeleteUserPreset(preset.Name);
        RefreshPresets();
        SelectedPreset = null;
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
    }

    partial void OnPreGainChanged(double value)
    {
        _audio.SetEqualizerPreGain(value);
    }

    // ===== Dynamic band set =====

    private void HookBand(EqualizerBand band) => band.PropertyChanged += OnBandChanged;
    private void UnhookBand(EqualizerBand band) => band.PropertyChanged -= OnBandChanged;

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EqualizerBand band) return;
        var index = Bands.IndexOf(band);
        if (index < 0) return;

        // "Listen to harmonic" solo: one band at a time, audio goes only through it.
        if (e.PropertyName == nameof(EqualizerBand.IsSolo))
        {
            if (band.IsSolo)
                foreach (var other in Bands)
                    if (!ReferenceEquals(other, band) && other.IsSolo)
                        other.IsSolo = false;
            _audio.SetEqualizerSolo(band.IsSolo ? band.Frequency : null, band.IsSolo ? band.Q : null);
            return;
        }

        // Whole band (frequency, gain, type, slope, Q).
        _audio.UpdateEqualizerBand(index, band);
        if (band.IsSolo)
            _audio.SetEqualizerSolo(band.Frequency, band.Q); // solo follows the point
    }

    /// <summary>Full replacement of the band set (preset). Empty set = flat line.</summary>
    private void ReplaceBands(System.Collections.Generic.IEnumerable<EqualizerBand> newBands)
    {
        foreach (var b in Bands) UnhookBand(b);
        Bands.Clear();
        var i = 0;
        foreach (var b in newBands)
        {
            b.Index = i++;
            HookBand(b);
            Bands.Add(b);
        }
        _audio.ApplyEqualizerBands(Bands);
        _audio.SetEqualizerSolo(null, null); // solo is reset with the set
    }

    /// <summary>Double-click on the chart: add a node at the clicked point.</summary>
    [RelayCommand]
    private void AddBand((double freq, double gain) args)
    {
        if (Bands.Count >= MaxBands) return;
        var band = new EqualizerBand
        {
            Frequency = Math.Clamp(args.freq, 20, 20000),
            Gain = Math.Clamp(args.gain, -12, 12)
        };
        HookBand(band);
        Bands.Add(band);
        _audio.ApplyEqualizerBands(Bands);
    }

    /// <summary>Double-click on a node: remove the band.</summary>
    [RelayCommand]
    private void RemoveBand(int index)
    {
        if (index < 0 || index >= Bands.Count) return;
        UnhookBand(Bands[index]);
        Bands.RemoveAt(index);
        for (int i = 0; i < Bands.Count; i++) Bands[i].Index = i;
        _audio.ApplyEqualizerBands(Bands);
    }

    [RelayCommand]
    private void ResetBands()
    {
        // "Reset" = an empty set: a flat line with no points.
        ReplaceBands(Array.Empty<EqualizerBand>());
        PreGain = 0;
    }

    private void ApplyPreset(EqualizerPreset preset)
    {
        // Selecting a preset means the user intends to hear it: enable the equalizer automatically.
        if (!IsEnabled) IsEnabled = true;
        PreGain = preset.PreGain;
        ReplaceBands(preset.Bands.Select(b => new EqualizerBand { Frequency = b.Frequency, Gain = b.Gain }));
    }

    [RelayCommand]
    private void SaveAsPreset(string name)
    {
        var preset = new EqualizerPreset
        {
            Name = name,
            PreGain = PreGain,
            Bands = Bands.Select(b => new EqualizerBand { Index = b.Index, Frequency = b.Frequency, Gain = b.Gain, Type = b.Type, SlopeDbOct = b.SlopeDbOct, Q = b.Q }).ToList()
        };
        _service.SaveUserPreset(preset);
        RefreshPresets();
    }
}
