using System.Collections.Generic;
using System.Linq;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.Audio;

/// <summary>
/// Stores equalizer presets and applies them to the AudioEngine.
/// User presets are serialized into settings.json
/// (AppSettings.EqualizerUserPresets) and survive restarts.
/// </summary>
public sealed class EqualizerService
{
    // null in unit tests: presets live in memory, persistence is unavailable.
    private readonly SettingsService? _settings;

    public IReadOnlyList<EqualizerPreset> BuiltInPresets { get; }

    public List<EqualizerPreset> UserPresets { get; } = new();

    public EqualizerService(SettingsService? settings = null)
    {
        _settings = settings;
        BuiltInPresets = new List<EqualizerPreset>
        {
            // Flat — empty band set: a straight line without points, like Pro-Q.
            new EqualizerPreset { Name = "Flat", IsBuiltIn = true },
            Build("Rock",       -1, 1, 2, 3, 1, -1, 0, 1, 2, 3),
            Build("Pop",         0, 0, 1, 3, 3, 1, 0, -1, -1, 0),
            Build("Classical",  -1, -1, 0, 1, 2, 2, 2, 1, 0, -1),
            Build("Jazz",       -1, 0, 1, 2, 2, 1, 1, 0, 0, -1),
            Build("Electronic",  1, 1, 0, 0, -1, -1, 0, 1, 2, 3),
            Build("Vocal",      -1, -1, 0, 2, 3, 3, 2, 1, 0, -1),
            Build("Bass Boost",  4, 3, 2, 1, 0, 0, 0, 0, 0, 0),
            Build("Treble Boost",0, 0, 0, 0, 0, 0, 1, 2, 3, 4),
        }.AsReadOnly();

        // User presets come from settings.json.
        if (_settings != null)
            foreach (var p in _settings.Current.EqualizerUserPresets)
                UserPresets.Add(p);
    }

    private static EqualizerPreset Build(string name, params double[] gains)
    {
        var preset = new EqualizerPreset { Name = name, IsBuiltIn = true, PreGain = 0 };
        for (int i = 0; i < EqualizerSampleProvider.BandFrequencies.Length; i++)
            preset.Bands.Add(new EqualizerBand
            {
                Index = i,
                Frequency = EqualizerSampleProvider.BandFrequencies[i],
                Gain = i < gains.Length ? gains[i] : 0
            });
        return preset;
    }

    public EqualizerPreset? FindPreset(string name)
        => BuiltInPresets.FirstOrDefault(p => p.Name == name)
        ?? UserPresets.FirstOrDefault(p => p.Name == name) as EqualizerPreset;

    public void SaveUserPreset(EqualizerPreset preset)
    {
        var existing = UserPresets.FirstOrDefault(p => p.Name == preset.Name);
        if (existing != null)
        {
            existing.PreGain = preset.PreGain;
            existing.Bands = preset.Bands;
        }
        else
        {
            preset.IsBuiltIn = false;
            UserPresets.Add(preset);
        }
        PersistUserPresets();
    }

    public bool DeleteUserPreset(string name)
    {
        var p = UserPresets.FirstOrDefault(x => x.Name == name);
        var removed = p != null && UserPresets.Remove(p);
        if (removed) PersistUserPresets();
        return removed;
    }

    private void PersistUserPresets()
    {
        if (_settings == null) return;
        _settings.Update(s => s.EqualizerUserPresets = UserPresets);
    }
}
