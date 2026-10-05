using System;
using System.Globalization;

namespace BatPlayer.Helpers;

/// <summary>
/// Единая «дата добавления в библиотеку» для треков всех источников: локальные файлы
/// берут её из tracks.date_added, платформенные карточки — из своих полей времени
/// (SoundCloud/Яндекс — время лайка, VK — момент первой синхронизации, Spotify —
/// added_at). LoadAsync сортирует объединённый список по ней, поэтому свежедобавленный
/// трек ЛЮБОЙ платформы встаёт над старыми треками остальных источников.
/// Все даты хранятся в БД текстом в разных форматах (ISO-8601 у ЯМ/Spotify/play_log,
/// «2017/05/25 10:23:45 +0000» у SoundCloud) — приводим к UTC DateTime.
/// </summary>
public static class TrackTimestamps
{
    public static DateTime ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return Normalize(dt);
        // SoundCloud пишет офсет без двоеточия («… +0000») — инвариантный TryParse его
        // берёт не всегда; отрезаем хвост и парсим время как есть (офсет 00:00 = UTC).
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
