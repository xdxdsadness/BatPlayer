using System;

namespace BatPlayer.Models;

/// <summary>
/// Один трек в библиотеке. Маппится на таблицу tracks в SQLite.
/// </summary>
public sealed class Track
{
    // Значения, которые старые версии писали в БД вместо пустых тегов.
    // Не меняются: в существующих записях они лежат в artist/album.
    private const string LegacyUnknownArtist = "Неизвестный исполнитель";
    private const string LegacyUnknownAlbum = "Неизвестный альбом";

    public long Id { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = "Неизвестный исполнитель";

    public string Album { get; set; } = "Неизвестный альбом";

    public string Genre { get; set; } = string.Empty;

    public int Year { get; set; }

    public int TrackNumber { get; set; }

    public long DurationTicks { get; set; }

    public int Bitrate { get; set; }

    public int SampleRate { get; set; }

    public int Channels { get; set; }

    public string Format { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public string? CoverCachePath { get; set; }

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    public DateTime? LastPlayed { get; set; }

    public int PlayCount { get; set; }

    public long LastPositionTicks { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsAvailable { get; set; } = true;

    public string CoverHash { get; set; } = string.Empty;

    // ===== Runtime-only поля (в таблице tracks их нет; выставляются при построении списков) =====

    /// <summary>Источник трека: "local" — файл библиотеки, "soundcloud" — лайк SoundCloud,
    /// "vk" — трек VK Music, "yandex" — трек Яндекс Музыки, "spotify" — трек Spotify.</summary>
    public const string SourceLocal = "local";
    public const string SourceSoundCloud = "soundcloud";
    public const string SourceVk = "vk";
    public const string SourceYandex = "yandex";
    public const string SourceSpotify = "spotify";

    public string Source { get; set; } = SourceLocal;

    /// <summary>
    /// Универсальный идентификатор трека платформы. Историческое имя поля — ScId
    /// (SoundCloud id); переименование ломает существующие обращения, поэтому поле
    /// оставлено как есть и используется для всех платформ: при Source="soundcloud"
    /// хранит числовой id SoundCloud, при Source="vk" — vk_id ("{owner_id}_{id}"),
    /// при Source="yandex" — ym_id (числовой id трека API Яндекс Музыки),
    /// при Source="spotify" — spotify_id (Base62 id трека Spotify API).
    /// </summary>
    public string ScId { get; set; } = string.Empty;

    /// <summary>Spotify track ID для удобного доступа (алиас ScId для Source="spotify").</summary>
    public string SpotifyId
    {
        get => Source == SourceSpotify ? ScId : string.Empty;
        set { if (Source == SourceSpotify) ScId = value; }
    }

    /// <summary>Удалённый платформенный источник (SoundCloud/VK/Яндекс Музыка/Spotify): FilePath может
    /// быть пуст и резолвится плеером на клике/переходе через AudioService.FilePathResolver.</summary>
    public bool IsPlatformTrack => Source is SourceSoundCloud or SourceVk or SourceYandex or SourceSpotify;

    /// <summary>
    /// Тот же ли это трек, что other. Для платформенных треков (SC/VK/Яндекс/Spotify)
    /// уникальность = источник + id на платформе (ScId): runtime-карточки одной и той же
    /// композиции в разных списках (Home/Favorites/страницы платформ) — разные объекты
    /// с разными (отрицательными) Id, а Id из РАЗНЫХ списков случайно совпадают
    /// (-1 - index), из-за чего сравнение по Id подсвечивало чужие карточки.
    /// Для локальных файлов — Id из БД.
    /// </summary>
    public bool IsSameTrackAs(Track? other)
    {
        if (other is null) return false;
        if (IsPlatformTrack != other.IsPlatformTrack) return false;
        if (IsPlatformTrack)
            return string.Equals(Source, other.Source, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(ScId)
                && string.Equals(ScId, other.ScId, StringComparison.Ordinal);
        return Id != 0 && Id == other.Id;
    }

    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);

    public TimeSpan LastPosition => TimeSpan.FromTicks(LastPositionTicks);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? System.IO.Path.GetFileNameWithoutExtension(FilePath)
        : Title;

    /// <summary>Исполнитель для отображения: пустые и legacy-значения заменяются локализованной строкой.</summary>
    public string DisplayArtist => IsUnknownValue(Artist, LegacyUnknownArtist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Альбом для отображения: пустые и legacy-значения заменяются локализованной строкой.</summary>
    public string DisplayAlbum => IsUnknownValue(Album, LegacyUnknownAlbum)
        ? Localization.Loc.Get("UnknownAlbum")
        : Album;

    private static bool IsUnknownValue(string value, string legacy) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, legacy, StringComparison.Ordinal);

    public string QualityDisplay
    {
        get
        {
            var parts = new List<string>();
            if (Bitrate > 0) parts.Add($"{Bitrate} kbps");
            if (SampleRate > 0) parts.Add($"{(SampleRate / 1000.0):0.0} kHz");
            if (Channels > 0) parts.Add(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels}ch");
            return string.Join(" · ", parts);
        }
    }
}
