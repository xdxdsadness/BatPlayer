using BatPlayer.Models;
using Xunit;

namespace BatPlayer.Tests.Models;

public class TrackTests
{
    [Fact]
    public void DisplayTitle_WhenTitleEmpty_UsesFileName()
    {
        var t = new Track { FilePath = "C:\\Music\\My Song.mp3", Title = "" };
        Assert.Equal("My Song", t.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_WhenTitleWhitespace_UsesFileName()
    {
        var t = new Track { FilePath = "/music/track.flac", Title = "   " };
        Assert.Equal("track", t.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_WhenTitleSet_UsesTitle()
    {
        var t = new Track { FilePath = "C:\\Music\\song.mp3", Title = "Real Title" };
        Assert.Equal("Real Title", t.DisplayTitle);
    }

    [Fact]
    public void QualityDisplay_FormatsBitrateAndSampleRate()
    {
        var t = new Track { Bitrate = 320, SampleRate = 44100, Channels = 2 };
        var q = t.QualityDisplay;
        Assert.Contains("320 kbps", q);
        Assert.Contains("44.1 kHz", q);
        Assert.Contains("stereo", q);
    }

    [Fact]
    public void QualityDisplay_MonoChannel_ShownAsMono()
    {
        var t = new Track { Bitrate = 128, SampleRate = 22050, Channels = 1 };
        Assert.Contains("mono", t.QualityDisplay);
    }

    [Fact]
    public void QualityDisplay_WhenEmpty_ReturnsEmptyString()
    {
        var t = new Track();
        Assert.Equal(string.Empty, t.QualityDisplay);
    }

    [Fact]
    public void Duration_ConvertsFromTicks()
    {
        var t = new Track { DurationTicks = System.TimeSpan.FromMinutes(3).Ticks };
        Assert.Equal(3 * 60, t.Duration.TotalSeconds);
    }

    [Fact]
    public void Defaults_AreReasonable()
    {
        var t = new Track();
        Assert.Equal("Неизвестный исполнитель", t.Artist);
        Assert.Equal("Неизвестный альбом", t.Album);
        Assert.True(t.IsAvailable);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var s = new AppSettings();
        Assert.Equal("en", s.Language);
        Assert.True(s.MinimizeToTray);
        Assert.True(s.CloseToTray);
        Assert.True(s.AutoResumePlayback);
        Assert.True(s.ConfirmDeletion);
        Assert.False(s.UseWasapiExclusive);
        Assert.False(s.EqualizerEnabled);
        Assert.Equal("Flat", s.CurrentEqualizerPreset);
        Assert.True(s.AnimationsEnabled);
        Assert.True(s.SidebarVisible);
    }

    [Fact]
    public void HotkeyDefaults_AreBound()
    {
        var s = new AppSettings();
        Assert.Equal(32, s.PlayPause.Key);  // Space
        Assert.Equal(70, s.Search.Key);     // F
        Assert.Equal(4, s.Search.Modifiers); // Ctrl
    }

    [Fact]
    public void CrossfadeDuration_DefaultIs3Seconds()
    {
        var s = new AppSettings();
        Assert.Equal(3000, s.CrossfadeDurationMs);
    }
}
