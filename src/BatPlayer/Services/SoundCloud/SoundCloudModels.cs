using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// DTO для неофициального web-API SoundCloud (api-v2.soundcloud.com).
/// Поля названы по snake_case JSON через JsonPropertyName; незнакомые поля игнорируются —
/// API регулярно добавляет новые, парсинг не должен падать.
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

/// <summary>Трек из collection[].track (лайки, /me/likes/tracks).</summary>
public sealed class ScTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Полная длительность в миллисекундах (int в API, long на всякий случай).</summary>
    [JsonPropertyName("duration")]
    public long DurationMs { get; set; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    [JsonPropertyName("permalink_url")]
    public string? PermalinkUrl { get; set; }

    [JsonPropertyName("streamable")]
    public bool Streamable { get; set; }

    /// <summary>Счётчик прослушиваний трека — фильтр ноунеймов в related-кандидатах
    /// волны (у «нейро-треков» и мусорных заливок он на порядки ниже порога).</summary>
    [JsonPropertyName("playback_count")]
    public long PlaybackCount { get; set; }

    /// <summary>"SNIPPET" — Go+-трек (полная версия только по подписке SoundCloud),
    /// "BLOCK" — недоступен в регионе; null — обычный проигрываемый трек.
    /// У Go+/заблокированных транскодинги всегда отвечают 404 — проверяем ДО сети.</summary>
    [JsonPropertyName("policy")]
    public string? Policy { get; set; }

    /// <summary>Модель монетизации: "AD_SUPPORTED" — трек с рекламой (SoundCloud
    /// отдаёт его ТОЛЬКО зашифрованным CENC/Widevine-стримом — обычные
    /// progressive/hls-транскодинги отвечают 404 даже залогиненным), "BLACKBOX" —
    /// обычный трек с открытыми стримами. Диагностика «почему не играет».</summary>
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

/// <summary>Элемент коллекции лайков: либо track, либо playlist (плейлисты пропускаем).</summary>
public sealed class ScLikeItem
{
    /// <summary>Дата лайка (не дата загрузки трека) — хранится в liked_at.</summary>
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

/// <summary>Ответ на GET {transcoding.url}?client_id=... — прямая mp3-ссылка.</summary>
public sealed class ScStreamResolve
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Ответ GET /search/tracks: {"collection":[…track…]} — для поиска
/// играбельной копии DRM-трека (перекачанные другими пользователями версии).
/// Отдельный «мягкий» DTO: в выдаче поиска поля бывают null (например,
/// playback_count) — строгий ScTrack падает на всём ответе из-за одного элемента.</summary>
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

/// <summary>Пользователь, чья веб-сессия подключена (GET /me).</summary>
public sealed class ScMeResponse
{
    /// <summary>Числовой id пользователя (в JSON — number); используется для /users/{id}/likes.</summary>
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

/// <summary>Ответ GET /tracks/{id}/related: {"collection":[…track…], "next_href":…} —
/// похожие треки SoundCloud (пул «сцены» для «Моей волны»).</summary>
public sealed class ScRelatedResponse
{
    [JsonPropertyName("collection")]
    public List<ScTrack> Collection { get; set; } = new();
}
