using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Contents of vk_auth.json: web-session cookies of vk.com/vk.ru and the VK user id.
/// AccessToken is a legacy field from the OAuth/Kate Mobile era: audio.get tokens are
/// dead (codes 3/8); kept only for reading old files, unused and optional.
/// The file holds session cookies — never print it to logs, store only locally.
/// </summary>
public sealed class VkAuthFile
{
    /// <summary>Cookie string "name=value; name=value" for vk.com/vk.ru (built by the login window).</summary>
    public string? CookieHeader { get; set; }
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    /// <summary>When the session was saved (after login).</summary>
    public string? SavedAt { get; set; }
    /// <summary>Last successful library sync (ISO, roundtrip).</summary>
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>Load/save/delete of vk_auth.json (%LOCALAPPDATA%/BatPlayer).</summary>
public sealed class VkAuthService(string path)
    : JsonAuthStore<VkAuthFile>(path, "VK");
