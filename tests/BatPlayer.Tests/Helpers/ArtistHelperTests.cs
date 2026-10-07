using BatPlayer.Helpers;
using Xunit;

namespace BatPlayer.Tests.Helpers;

public class ArtistHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Неизвестный исполнитель")]
    [InlineData("Неизвестный артист")]
    [InlineData("Unknown Artist")]
    [InlineData(" Неизвестный исполнитель ")]
    public void Key_LegacyOrEmptyValues_GluedIntoOneUnknownKey(string? artist)
    {
        Assert.Equal(string.Empty, ArtistHelper.Key(artist));
    }

    [Fact]
    public void Key_RealName_TrimmedAndLowercased()
    {
        // Key is case-insensitive: "Kai Angel" and "kai angel" are one artist.
        Assert.Equal("radiohead", ArtistHelper.Key(" Radiohead "));
        Assert.Equal(ArtistHelper.Key("Kai Angel"), ArtistHelper.Key("kai angel"));
    }

    [Fact]
    public void Key_LegacyValueAsSubstring_KeptAsRegularArtist()
    {
        // Mere similarity to the legacy string must not merge real artists.
        Assert.Equal("unknown artist band", ArtistHelper.Key("Unknown Artist Band"));
    }

    [Fact]
    public void DisplayName_EmptyKey_ReturnsLocalizedUnknownArtist()
    {
        Assert.Equal("Unknown Artist", ArtistHelper.DisplayName(string.Empty));
    }

    [Fact]
    public void DisplayName_RealKey_ReturnsKeyAsIs()
    {
        Assert.Equal("Radiohead", ArtistHelper.DisplayName("Radiohead"));
    }
}
