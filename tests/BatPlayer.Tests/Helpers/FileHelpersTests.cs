using BatPlayer.Helpers;
using Xunit;

namespace BatPlayer.Tests.Helpers;

public class FileHelpersTests
{
    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.MP3", true)]
    [InlineData("lossless.flac", true)]
    [InlineData("audio.wav", true)]
    [InlineData("track.ogg", true)]
    [InlineData("track.opus", true)]
    [InlineData("track.m4a", true)]
    [InlineData("track.aac", true)]
    [InlineData("track.aiff", true)]
    [InlineData("track.aif", true)]
    [InlineData("video.mp4", false)]
    [InlineData("image.png", false)]
    [InlineData("document.pdf", false)]
    [InlineData("noext", false)]
    public void IsAudioFile_DetectsExtension(string path, bool expected)
    {
        Assert.Equal(expected, FileHelpers.IsAudioFile(path));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.00 GB")]
    public void FormatFileSize_FormatsCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, FileHelpers.FormatFileSize(bytes));
    }

    [Fact]
    public void FormatTime_UnderHour_ShowsMinutesSeconds()
    {
        Assert.Equal("03:45", FileHelpers.FormatTime(System.TimeSpan.FromSeconds(225)));
    }

    [Fact]
    public void FormatTime_OverHour_ShowsHoursMinutesSeconds()
    {
        Assert.Equal("1:02:03", FileHelpers.FormatTime(System.TimeSpan.FromSeconds(3723)));
    }

    [Fact]
    public void FormatBitrate_WithZero_ReturnsDash()
    {
        Assert.Equal("—", FileHelpers.FormatBitrate(0));
    }

    [Fact]
    public void FormatBitrate_WithValue_ReturnsKbps()
    {
        Assert.Equal("320 kbps", FileHelpers.FormatBitrate(320));
    }
}
