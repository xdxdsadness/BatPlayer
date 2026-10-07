using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Contents of sc_auth.json: SoundCloud web-session cookies and the last valid client_id.
/// The file holds access credentials — never print it to logs, store only locally.
/// </summary>
public sealed class SoundCloudAuthFile
{
    public string? Cookies { get; set; }
    public string? ClientId { get; set; }
    /// <summary>User id (GET /me, field id) — cache for /users/{id}/likes so /me is not hit every time.</summary>
    public string? UserId { get; set; }
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>Load/save/delete of sc_auth.json (%LOCALAPPDATA%/BatPlayer).</summary>
public sealed class SoundCloudAuthStore(string path)
    : JsonAuthStore<SoundCloudAuthFile>(path, "SoundCloud");
