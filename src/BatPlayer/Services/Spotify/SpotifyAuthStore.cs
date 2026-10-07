using BatPlayer.Services;

namespace BatPlayer.Services.Spotify;

/// <summary>Atomic load/save of spotify_auth.json (tokens + profile + last sync stamp).</summary>
public sealed class SpotifyAuthStore(string path)
    : JsonAuthStore<SpotifyAuthFile>(path, "Spotify");
