using System;
using System.Globalization;

namespace BatPlayer.Helpers;

/// <summary>
/// Unified "added to library" date for tracks of all sources: local files take it from
/// tracks.date_added, platform cards from their own time fields (SoundCloud/Yandex —
/// like time, VK — first sync moment, Spotify — added_at). LoadAsync sorts the merged
/// list by it, so a newly added track of ANY platform lands above older tracks of the
/// other sources. Dates are stored in the DB as text in different formats (ISO-8601 for
/// YM/Spotify/play_log, "2017/05/25 10:23:45 +0000" for SoundCloud) — parsed to UTC DateTime.
/// </summary>
public static class TrackTimestamps
{
    public static DateTime ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return Normalize(dt);
        // SoundCloud writes the offset without a colon ("... +0000") — the invariant
        // TryParse does not always take it; strip the tail and parse the time as-is (offset 00:00 = UTC).
        if (value.Length > 6 && value[^5] is '+' or '-'
            && DateTime.TryParse(value[..^5].TrimEnd(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return Normalize(dt);
        return DateTime.MinValue;
    }

    private static DateTime Normalize(DateTime dt)
        => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
}
