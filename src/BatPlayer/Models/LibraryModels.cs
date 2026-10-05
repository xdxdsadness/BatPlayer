using System.Collections.ObjectModel;

namespace BatPlayer.Models;

public sealed class Artist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public int AlbumCount { get; set; }
    public string? CoverCachePath { get; set; }
}

public sealed class Album
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackCount { get; set; }
    public string? CoverCachePath { get; set; }
}

public sealed class Genre
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TrackCount { get; set; }
}

public sealed class Folder
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool AutoScan { get; set; } = true;
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "Новый плейлист";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int TrackCount { get; set; }
    // Имя строго под колонку cover_path: Dapper (MatchNamesWithUnderscores)
    // маппит её именно в CoverPath; под именем CoverCachePath значение
    // молча терялось, и обложка плейлиста никогда не показывалась.
    public string? CoverPath { get; set; }
}

public sealed class PlaylistTrack
{
    public long Id { get; set; }
    public long PlaylistId { get; set; }
    public long TrackId { get; set; }
    public int Position { get; set; }
    public Track? Track { get; set; }
}
