using System;
using BatPlayer.Helpers;
using Xunit;

namespace BatPlayer.Tests.Helpers;

/// <summary>
/// Парсер единой «даты добавления» для сортировки объединённых списков
/// (локальные + платформенные): разные форматы БД → один UTC DateTime.
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
        // Формат created_at у SoundCloud API: «2017/05/25 10:23:45 +0000»
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
        // Пустые/битые даты уходят вниз списка — так же, как NULL liked_at в SQL-порядке.
        Assert.Equal(DateTime.MinValue, TrackTimestamps.ParseUtc(value));
    }
}
