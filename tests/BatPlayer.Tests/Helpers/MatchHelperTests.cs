using System.Collections.Generic;
using BatPlayer.Helpers;
using BatPlayer.Models;
using Xunit;

namespace BatPlayer.Tests.Helpers;

/// <summary>
/// Normalization and matching of SoundCloud tracks against the local library.
/// </summary>
public class MatchHelperTests
{
    private static Track Local(string artist, string title) => new()
    {
        Artist = artist,
        Title = title,
        FilePath = $@"C:\music\{artist} - {title}.mp3"
    };

    [Theory]
    [InlineData("Neon Fox", "neon fox")]
    [InlineData("NEON FOX", "neon fox")]
    [InlineData("Midnight Drive (Official Video)", "midnight drive")]
    [InlineData("Track [HD 2026]", "track")]
    [InlineData("Song {Remastered}", "song")]
    [InlineData("Song feat. Someone Else", "song")]
    [InlineData("Song ft. Someone", "song")]
    [InlineData("Song Featuring Someone", "song")]
    [InlineData("Hello—World!", "hello world")]
    [InlineData("  Multiple   Spaces  ", "multiple spaces")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("Кино - Группа крови", "кино группа крови")]
    public void Normalize_StripsNoiseAndRepeats(string input, string expected)
        => Assert.Equal(expected, MatchHelper.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(feat. Nobody)")]
    public void Normalize_EmptyishInput_ReturnsEmptyString(string? input)
        => Assert.Equal(string.Empty, MatchHelper.Normalize(input));

    [Fact]
    public void NormalizeArtist_UsesArtistHelperKeyForLegacyUnknowns()
    {
        // Legacy values of old DB records collapse into the same key as an empty string.
        Assert.Equal(MatchHelper.NormalizeArtist(""), MatchHelper.NormalizeArtist("Неизвестный исполнитель"));
        Assert.Equal(MatchHelper.NormalizeArtist(""), MatchHelper.NormalizeArtist("Unknown Artist"));
        Assert.Equal("neon fox", MatchHelper.NormalizeArtist("Neon Fox"));
    }

    [Fact]
    public void BuildKey_EqualForDifferentlyWrittenSameTrack()
    {
        var a = MatchHelper.BuildKey("Neon Fox", "Midnight Drive (Official Video)");
        var b = MatchHelper.BuildKey("neon fox", "Midnight  Drive");

        Assert.Equal(a, b);
    }

    [Fact]
    public void FindLocalMatch_ExactArtistAndNormalizedTitle()
    {
        var local = new List<Track> { Local("Neon Fox", "Midnight Drive") };

        var match = MatchHelper.FindLocalMatch(local, "Neon Fox", "Midnight Drive (Official Video)");

        Assert.NotNull(match);
        Assert.Equal("Midnight Drive", match!.Title);
    }

    [Fact]
    public void FindLocalMatch_IgnoresFeatAndCase()
    {
        var local = new List<Track> { Local("Neon Fox", "Midnight Drive") };

        var match = MatchHelper.FindLocalMatch(local, "NEON FOX", "Midnight Drive feat. Someone");

        Assert.NotNull(match);
    }

    [Fact]
    public void FindLocalMatch_SplitEmbeddedTitle_Matches()
    {
        // SoundCloud often returns titles as "Artist - Title".
        var local = new List<Track> { Local("Neon Fox", "Midnight Drive") };

        var match = MatchHelper.FindLocalMatch(local, "Neon Fox", "Neon Fox - Midnight Drive");

        Assert.NotNull(match);
    }

    [Fact]
    public void FindLocalMatch_DifferentArtist_ReturnsNull()
    {
        var local = new List<Track> { Local("Neon Fox", "Midnight Drive") };

        Assert.Null(MatchHelper.FindLocalMatch(local, "Other Artist", "Midnight Drive"));
    }

    [Fact]
    public void FindLocalMatch_DifferentTitle_ReturnsNull()
    {
        var local = new List<Track> { Local("Neon Fox", "Midnight Drive") };

        Assert.Null(MatchHelper.FindLocalMatch(local, "Neon Fox", "Morning Walk"));
    }

    [Fact]
    public void FindLocalMatch_EmptyLibrary_ReturnsNull()
        => Assert.Null(MatchHelper.FindLocalMatch(new List<Track>(), "Neon Fox", "Midnight Drive"));

    [Fact]
    public void FindLocalMatch_PicksCorrectTrackAmongMany()
    {
        var local = new List<Track>
        {
            Local("Other Artist", "Midnight Drive"),
            Local("Neon Fox", "Other Song"),
            Local("Neon Fox", "Midnight Drive"),
        };

        var match = MatchHelper.FindLocalMatch(local, "Neon Fox", "Midnight Drive (Original Mix)");

        Assert.NotNull(match);
        Assert.Equal("Neon Fox", match!.Artist);
        Assert.Equal("Midnight Drive", match.Title);
    }
}
