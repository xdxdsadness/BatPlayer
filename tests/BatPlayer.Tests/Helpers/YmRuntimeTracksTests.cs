using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Models;
using Xunit;

namespace BatPlayer.Tests.Helpers;

public class YmRuntimeTracksTests
{
    private static YmTrackRow Row(int i) => new()
    {
        YmId = $"{1000 + i}",
        Title = $"Track {i}",
        Artist = $"Artist {i}",
        DurationMs = 200_000 + i,
        Available = true
    };

    [Theory]
    [InlineData("All")]
    [InlineData("Favorites")]
    public void BuildYmAppend_AllAndFavorites_ReturnAllLikes(string filterMode)
    {
        var rows = Enumerable.Range(0, 5).Select(Row).ToList();

        var result = YmRuntimeTracks.BuildYmAppend(filterMode, rows, startIndex: 7);

        Assert.Equal(5, result.Count);
        Assert.Equal(-1 - 7, result[0].Id);
        Assert.Equal("1000", result[0].ScId);
        Assert.Equal(Track.SourceYandex, result[0].Source);
    }

    [Theory]
    [InlineData("Recent")]
    [InlineData("Played")]
    public void BuildYmAppend_RecentAndPlayed_ReturnEmpty(string filterMode)
    {
        var rows = Enumerable.Range(0, 5).Select(Row).ToList();

        var result = YmRuntimeTracks.BuildYmAppend(filterMode, rows, startIndex: 0);

        Assert.Empty(result);
    }
}
