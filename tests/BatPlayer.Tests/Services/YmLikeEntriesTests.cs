using BatPlayer.Services.YandexMusic;
using Xunit;

namespace BatPlayer.Tests.Services;

public class YmLikeEntriesTests
{
    [Fact]
    public void ParseLikeEntries_LibraryLayout_ReturnsIdsWithTimestamps()
    {
        const string json = """
            {"result":{"library":{"uid":1,"revision":2,"totalTracks":2,"tracks":[
                {"id":"153000497","timestamp":"2026-09-30T15:20:20+00:00"},
                {"id":"156107653","timestamp":"2026-09-27T00:05:01+00:00"}
            ]}}}
            """;

        var entries = YmJsonParser.ParseLikeEntries(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal("153000497", entries[0].Id);
        Assert.Equal("2026-09-30T15:20:20+00:00", entries[0].LikedAt);
        Assert.Equal("156107653", entries[1].Id);
    }

    [Fact]
    public void ParseLikeEntries_MissingTimestamp_ReturnsEmptyLikedAt()
    {
        const string json = """
            {"result":{"library":{"tracks":[{"id":"1"},{"id":2}]}}}
            """;

        var entries = YmJsonParser.ParseLikeEntries(json);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(string.Empty, e.LikedAt));
    }

    [Fact]
    public void ParseLikeIds_Wrapper_ReturnsIdsOnly()
    {
        const string json = """
            {"result":{"library":{"tracks":[{"id":"10","timestamp":"2026-09-30T15:20:20+00:00"}]}}}
            """;

        Assert.Equal(new[] { "10" }, YmJsonParser.ParseLikeIds(json));
    }

    [Fact]
    public void ParseLikeEntries_BrokenJson_ReturnsEmpty()
    {
        Assert.Empty(YmJsonParser.ParseLikeEntries("{not json"));
        Assert.Empty(YmJsonParser.ParseLikeEntries(null));
    }
}
