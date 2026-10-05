using System;
using System.Text.Json.Serialization;

namespace BatPlayer.Services.Spotify;

/// <summary>Содержимое spotify_auth.json: OAuth-токены и профиль аккаунта.</summary>
public sealed class SpotifyAuthFile
{
    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    /// <summary>Момент истечения access_token (ISO 8601, UTC).</summary>
    public string? ExpiresAt { get; set; }

    public string? UserId { get; set; }

    public string? DisplayName { get; set; }

    public string? Email { get; set; }

    public string? SavedAt { get; set; }

    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>Ответ https://accounts.spotify.com/api/token.</summary>
public sealed class SpotifyTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

/// <summary>Страница /v1/me/tracks.</summary>
public sealed class SpotifySavedTracksResponse
{
    [JsonPropertyName("href")]
    public string Href { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public System.Collections.Generic.List<SpotifySavedTrackItem> Items { get; set; } = new();

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("previous")]
    public string? Previous { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>Элемент saved track: дата добавления + сам трек.</summary>
public sealed class SpotifySavedTrackItem
{
    [JsonPropertyName("added_at")]
    public string? AddedAt { get; set; }

    [JsonPropertyName("track")]
    public SpotifyTrack? Track { get; set; }
}

/// <summary>Трек /v1/me/tracks (используются только нужные поля).</summary>
public sealed class SpotifyTrack
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }

    /// <summary>Локальный файл пользователя в библиотеке Spotify: приходит без Id
    /// и ломает подсчёт страниц — при синке пропускается.</summary>
    [JsonPropertyName("is_local")]
    public bool IsLocal { get; set; }

    [JsonPropertyName("is_playable")]
    public bool? IsPlayable { get; set; }

    [JsonPropertyName("popularity")]
    public int Popularity { get; set; }

    [JsonPropertyName("artists")]
    public System.Collections.Generic.List<SpotifyArtist>? Artists { get; set; }

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }

    [JsonPropertyName("external_urls")]
    public SpotifyExternalUrls? ExternalUrls { get; set; }
}

public sealed class SpotifyArtist
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

public sealed class SpotifyAlbum
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public System.Collections.Generic.List<SpotifyImage>? Images { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}

public sealed class SpotifyImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }
}

public sealed class SpotifyExternalUrls
{
    [JsonPropertyName("spotify")]
    public string Spotify { get; set; } = string.Empty;
}

/// <summary>Профиль /v1/me — заполняется после первого подключения.</summary>
public sealed class SpotifyUserProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>Ошибка Web API с HTTP-статусом (401/403 — токен отозван).</summary>
public sealed class SpotifyApiException : Exception
{
    public int StatusCode { get; }

    public SpotifyApiException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
