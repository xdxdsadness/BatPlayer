using System.Linq;
using BatPlayer.Audio;
using BatPlayer.Models;
using Xunit;

namespace BatPlayer.Tests.Audio;

public class EqualizerServiceTests
{
    [Fact]
    public void BuiltInPresets_ContainAllExpectedNames()
    {
        var svc = new EqualizerService();
        var names = svc.BuiltInPresets.Select(p => p.Name).ToList();

        Assert.Contains("Flat", names);
        Assert.Contains("Rock", names);
        Assert.Contains("Pop", names);
        Assert.Contains("Classical", names);
        Assert.Contains("Jazz", names);
        Assert.Contains("Electronic", names);
        Assert.Contains("Vocal", names);
        Assert.Contains("Bass Boost", names);
        Assert.Contains("Treble Boost", names);
    }

    [Fact]
    public void BuiltInPresets_AllExceptFlatHave10Bands()
    {
        var svc = new EqualizerService();
        foreach (var preset in svc.BuiltInPresets.Where(p => p.Name != "Flat"))
            Assert.Equal(10, preset.Bands.Count);
    }

    [Fact]
    public void FlatPreset_HasNoBands_FlatLineWithoutNodes()
    {
        var svc = new EqualizerService();
        var flat = svc.BuiltInPresets.First(p => p.Name == "Flat");
        Assert.Empty(flat.Bands);
    }

    [Fact]
    public void SaveUserPreset_AddsToList()
    {
        var svc = new EqualizerService();
        var preset = new EqualizerPreset { Name = "MyPreset", PreGain = 0 };
        for (int i = 0; i < 10; i++)
            preset.Bands.Add(new EqualizerBand { Index = i, Frequency = 1000 * (i + 1), Gain = 0 });

        svc.SaveUserPreset(preset);
        Assert.Contains(svc.UserPresets, p => p.Name == "MyPreset");
    }

    [Fact]
    public void SaveUserPreset_WithSameName_UpdatesExisting()
    {
        var svc = new EqualizerService();
        var p1 = new EqualizerPreset { Name = "Dup", PreGain = 1 };
        p1.Bands.AddRange(Enumerable.Range(0, 10).Select(i => new EqualizerBand { Index = i, Frequency = i * 1000, Gain = 0 }));
        svc.SaveUserPreset(p1);

        var p2 = new EqualizerPreset { Name = "Dup", PreGain = 5 };
        p2.Bands.AddRange(Enumerable.Range(0, 10).Select(i => new EqualizerBand { Index = i, Frequency = i * 1000, Gain = 0 }));
        svc.SaveUserPreset(p2);

        Assert.Single(svc.UserPresets);
        Assert.Equal(5, svc.UserPresets[0].PreGain);
    }

    [Fact]
    public void FindPreset_ReturnsBuiltInOrUser_ByName()
    {
        var svc = new EqualizerService();
        Assert.NotNull(svc.FindPreset("Rock"));
        Assert.Null(svc.FindPreset("NonExistent"));
    }

    [Fact]
    public void DeleteUserPreset_RemovesFromList()
    {
        var svc = new EqualizerService();
        var preset = new EqualizerPreset { Name = "ToDelete" };
        preset.Bands.AddRange(Enumerable.Range(0, 10).Select(i => new EqualizerBand { Index = i, Frequency = i * 1000, Gain = 0 }));
        svc.SaveUserPreset(preset);

        Assert.True(svc.DeleteUserPreset("ToDelete"));
        Assert.Empty(svc.UserPresets);
    }

    [Fact]
    public void DeleteUserPreset_OnNonExistent_ReturnsFalse()
    {
        var svc = new EqualizerService();
        Assert.False(svc.DeleteUserPreset("DoesNotExist"));
    }
}
