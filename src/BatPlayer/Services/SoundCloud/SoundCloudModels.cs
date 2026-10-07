using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// DTOs for the unofficial SoundCloud web API (api-v2.soundcloud.com).
/// Fields are mapped to snake_case JSON via JsonPropertyName; unknown fields are ignored —
/// the API regularly adds new ones, so parsing must not fail.
/// </summary>

public sealed class ScUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;
}

public sealed class ScTranscodingFormat
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = string.Empty;

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = string.Empty;
}

public sealed class ScTranscoding
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public ScTranscodingFormat? Format { get; set; }

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = string.Empty;
}

public sealed class ScMedia
{
    [JsonPropertyName("transcodings")]
    public List<ScTranscoding> Transcodings { get; set; } = new();
}

/// <summary>A track from collection[].track (likes, /me/likes/tracks).</summary>
public sealed class ScTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Full duration in milliseconds (int in the API, long for safety).</summary>
    [JsonPropertyName("duration")]
    public long DurationMs { get; set; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    [JsonPropertyName("permalink_url")]
    public string? PermalinkUrl { get; set; }

    [JsonPropertyName("streamable")]
    public bool Streamable { get; set; }

    /// <summary>Track playback count — filters no-name uploads out of related-track
    /// candidates (AI tracks and junk uploads sit orders of magnitude below the threshold).</summary>
    [JsonPropertyName("playback_count")]
    public long PlaybackCount { get; set; }

    /// <summary>"SNIPPET" — Go+ track (full version only with a SoundCloud subscription),
    /// "BLOCK" — unavailable in the region; null — a normally playable track.
    /// Go+/blocked tracks' transcodings always answer 404 — checked BEFORE the network.</summary>
    [JsonPropertyName("policy")]
    public string? Policy { get; set; }

    /// <summary>Monetization model: "AD_SUPPORTED" — an ad-supported track (SoundCloud
    /// serves it ONLY as an encrypted CENC/Widevine stream — regular progressive/hls
    /// transcodings answer 404 even when logged in), "BLACKBOX" — a normal track with
    /// open streams. Diagnoses "why it won't play".</summary>
    [JsonPropertyName("monetization_model")]
    public string? MonetizationModel { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("user")]
    public ScUser? User { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("media")]
    public ScMedia? Media { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
}

/// <summary>A likes collection item: either a track or a playlist (playlists are skipped).</summary>
public sealed class ScLikeItem
{
    /// <summary>Like date (not the track's upload date) — stored in liked_at.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("track")]
    public ScTrack? Track { get; set; }

    [JsonPropertyName("playlist")]
    public ScTrack? Playlist { get; set; }
}

public sealed class ScLikesResponse
{
    [JsonPropertyName("collection")]
    public List<ScLikeItem> Collection { get; set; } = new();

    [JsonPropertyName("next_href")]
    public string? NextHref { get; set; }
}

/// <summary>Response to GET {transcoding.url}?client_id=... — a direct mp3 link.</summary>
public sealed class ScStreamResolve
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>GET /search/tracks response: {"collection":[…tracks…]} — used to find a
/// playable copy of a DRM track (versions re-uploaded by other users). A separate "soft"
/// DTO: search-result fields can be null (e.g. playback_count), and strict ScTrack would
/// fail the whole response over a single element.</summary>
public sealed class ScSearchResponse
{
    [JsonPropertyName("collection")]
    public List<ScSearchTrack> Collection { get; set; } = new();
}

public sealed class ScSearchTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("duration")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("streamable")]
    public bool? Streamable { get; set; }

    [JsonPropertyName("playback_count")]
    public long? PlaybackCount { get; set; }

    [JsonPropertyName("policy")]
    public string? Policy { get; set; }

    [JsonPropertyName("monetization_model")]
    public string? MonetizationModel { get; set; }

    [JsonPropertyName("user")]
    public ScUser? User { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

/// <summary>The user whose web session is connected (GET /me).</summary>
public sealed class ScMeResponse
{
    /// <summary>Numeric user id (a number in JSON); used for /users/{id}/likes.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("urn")]
    public string? Urn { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

/// <summary>GET /tracks/{id}/related response: {"collection":[…tracks…], "next_href":…} —
/// SoundCloud's similar tracks (the "scene" pool for My Wave).</summary>
public sealed class ScRelatedResponse
{
    [JsonPropertyName("collection")]
    public List<ScTrack> Collection { get; set; } = new();
}
