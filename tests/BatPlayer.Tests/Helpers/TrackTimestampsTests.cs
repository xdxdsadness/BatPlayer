using System;
using BatPlayer.Helpers;
using Xunit;

namespace BatPlayer.Tests.Helpers;

/// <summary>
/// Parser for the unified "date added" used to sort merged lists (local + platform):
/// various DB formats → a single UTC DateTime.
/// </summary>
public class TrackTimestampsTests
{
    [Fact]
    public void Iso8601_Parses()
    {
        var dt = TrackTimestamps.ParseUtc("2026-09-01T12:00:00Z");
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), dt);
    }

    [Fact]
    public void SoundCloudFormat_OffsetWithoutColon_Parses()
    {
        // SoundCloud API created_at format: "2017/05/25 10:23:45 +0000"
        var dt = TrackTimestamps.ParseUtc("2017/05/25 10:23:45 +0000");
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(new DateTime(2017, 5, 25, 10, 23, 45, DateTimeKind.Utc), dt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date at all")]
    public void Invalid_FallsBackToMinValue(string? value)
    {
        // Empty/broken dates sort to the bottom, like NULL liked_at in SQL order.
        Assert.Equal(DateTime.MinValue, TrackTimestamps.ParseUtc(value));
    }
}
