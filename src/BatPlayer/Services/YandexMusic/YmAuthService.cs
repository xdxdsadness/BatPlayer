using BatPlayer.Services;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Contents of ym_auth.json: Yandex ID OAuth token (implicit flow with the official
/// Yandex Music client_id) and account data. CookieHeader is a legacy field from the
/// cookies-session era: api.music.yandex.net answers Session_id cookies with 401, the
/// field is unused and always empty (kept for reading old files).
/// The file holds an access token — never print it to logs, store only locally.
/// </summary>
public sealed class YmAuthFile
{
    /// <summary>Yandex ID OAuth token (from the "#access_token=…" fragment of oauth.yandex.ru).</summary>
    public string? AccessToken { get; set; }

    /// <summary>Legacy web-session cookies of .yandex.ru: not accepted by the API, always empty.</summary>
    public string? CookieHeader { get; set; }

    /// <summary>Yandex Music account uid (from account/status at login); needed for likes/tracks.</summary>
    public string? Uid { get; set; }

    /// <summary>Account display name (for the Settings status); null when unknown.</summary>
    public string? DisplayName { get; set; }

    /// <summary>When the session was saved (ISO-8601, UtcNow).</summary>
    public string? SavedAt { get; set; }

    /// <summary>Last successful catalog sync (ISO-8601, UtcNow).</summary>
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>Load/save/delete of ym_auth.json (%LOCALAPPDATA%/BatPlayer).</summary>
public sealed class YmAuthService(string path)
    : JsonAuthStore<YmAuthFile>(path, "Yandex Music");
