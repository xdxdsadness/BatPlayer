using System;

namespace BatPlayer.Models;

/// <summary>
/// One track in the library. Maps to the SQLite tracks table.
/// </summary>
public sealed class Track
{
    // Values older app versions wrote to the DB instead of empty tags.
    // Do not change: existing rows store them in artist/album.
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

    // ===== Runtime-only fields (not in the tracks table; set when building lists) =====

    /// <summary>Track source: "local" — library file, "soundcloud" — SoundCloud like,
    /// "vk" — VK Music track, "yandex" — Yandex Music track, "spotify" — Spotify track.</summary>
    public const string SourceLocal = "local";
    public const string SourceSoundCloud = "soundcloud";
    public const string SourceVk = "vk";
    public const string SourceYandex = "yandex";
    public const string SourceSpotify = "spotify";

    public string Source { get; set; } = SourceLocal;

    /// <summary>
    /// Universal platform track identifier. The field name ScId is historical
    /// (SoundCloud id); renaming would break existing call sites, so the field is kept
    /// as-is and used for all platforms: Source="soundcloud" — numeric SoundCloud id,
    /// Source="vk" — vk_id ("{owner_id}_{id}"), Source="yandex" — ym_id (numeric id of
    /// the Yandex Music API), Source="spotify" — spotify_id (Base62 id of the Spotify API).
    /// </summary>
    public string ScId { get; set; } = string.Empty;

    /// <summary>Spotify track ID for convenient access (alias of ScId for Source="spotify").</summary>
    public string SpotifyId
    {
        get => Source == SourceSpotify ? ScId : string.Empty;
        set { if (Source == SourceSpotify) ScId = value; }
    }

    /// <summary>Remote platform source (SoundCloud/VK/Yandex Music/Spotify): FilePath may
    /// be empty and is resolved by the player on click/advance via AudioService.FilePathResolver.</summary>
    public bool IsPlatformTrack => Source is SourceSoundCloud or SourceVk or SourceYandex or SourceSpotify;

    /// <summary>
    /// Whether this is the same track as other. For platform tracks (SC/VK/Yandex/Spotify)
    /// uniqueness = source + platform id (ScId): runtime cards of the same composition in
    /// different lists (Home/Favorites/platform pages) are different objects with different
    /// (negative) Ids, and Ids from DIFFERENT lists accidentally coincide (-1 - index),
    /// which made Id comparison highlight the wrong cards. For local files — the DB Id.
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

    /// <summary>Display artist: empty and legacy values are replaced by the localized string.</summary>
    public string DisplayArtist => IsUnknownValue(Artist, LegacyUnknownArtist)
        ? Localization.Loc.Get("UnknownArtist")
        : Artist;

    /// <summary>Display album: empty and legacy values are replaced by the localized string.</summary>
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
