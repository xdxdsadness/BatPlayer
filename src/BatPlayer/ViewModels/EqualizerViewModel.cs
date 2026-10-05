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
/// Эквалайзер с динамическим набором полос (в духе FabFilter Pro-Q):
/// при старте полос нет — прямая линия; пользователь добавляет точки двойным
/// кликом по графику, удаляет двойным кликом по точке, перетаскиванием задаёт
/// частоту и усиление. Каждый факт изменения сразу уходит в аудиосервис.
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

        // VM живёт столько же, сколько приложение — отписка не нужна.
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
        // Полосы при старте НЕ восстанавливаем: по механике Pro-Q пользователь
        // начинает с прямой линии и расставляет точки сам; пресет применяется
        // только когда выбран в списке.
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

    /// <summary>Удалять можно только пользовательские пресеты; встроенные — навсегда.</summary>
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

    // ===== Динамический набор полос =====

    private void HookBand(EqualizerBand band) => band.PropertyChanged += OnBandChanged;
    private void UnhookBand(EqualizerBand band) => band.PropertyChanged -= OnBandChanged;

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EqualizerBand band) return;
        var index = Bands.IndexOf(band);
        if (index < 0) return;

        // Соло «слушать гармонику»: одна полоса за раз, звук идёт только через неё.
        if (e.PropertyName == nameof(EqualizerBand.IsSolo))
        {
            if (band.IsSolo)
                foreach (var other in Bands)
                    if (!ReferenceEquals(other, band) && other.IsSolo)
                        other.IsSolo = false;
            _audio.SetEqualizerSolo(band.IsSolo ? band.Frequency : null, band.IsSolo ? band.Q : null);
            return;
        }

        // Полоса целиком (частота, усиление, тип, крутизна, Q).
        _audio.UpdateEqualizerBand(index, band);
        if (band.IsSolo)
            _audio.SetEqualizerSolo(band.Frequency, band.Q); // соло следует за точкой
    }

    /// <summary>Полная замена набора полос (пресет). Пустой набор = прямая линия.</summary>
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
        _audio.SetEqualizerSolo(null, null); // соло сбрасывается вместе с набором
    }

    /// <summary>Двойной клик по графику: добавить узел в точке клика.</summary>
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

    /// <summary>Двойной клик по узлу: удалить полосу.</summary>
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
        // «Сбросить» = пустой набор: ровная линия без точек.
        ReplaceBands(Array.Empty<EqualizerBand>());
        PreGain = 0;
    }

    private void ApplyPreset(EqualizerPreset preset)
    {
        // Выбор пресета означает намерение слышать его: эквалайзер включается сам.
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
