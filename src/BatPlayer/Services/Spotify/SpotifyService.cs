using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Database;

namespace BatPlayer.Services.Spotify;

/// <summary>
/// Spotify Web API client: OAuth 2.0 Authorization Code + PKCE, token exchange,
/// auto-refresh, Saved Tracks (Liked Songs) sync with pages of 50.
///
/// The Client ID is not hardcoded (older builds shipped a YOUR_SPOTIFY_CLIENT_ID
/// placeholder that made Spotify answer "client_id: Invalid"): it is read from
/// %LOCALAPPDATA%/BatPlayer/spotify_client.json and can be entered in the login
/// window (SpotifyLoginWindow) without rebuilding.
/// </summary>
public sealed class SpotifyService
{
    public const string AuthFileName = "spotify_auth.json";
    public const string ClientIdFileName = "spotify_client.json";

    private const string ApiBase = "https://api.spotify.com/v1";
    private const string AccountsBase = "https://accounts.spotify.com";
    private const int PageSize = 50;

    internal const string RedirectUri = "http://localhost:8888/callback";

    private static readonly string[] Scopes =
    {
        "user-library-read",
        "playlist-read-private",
        "playlist-read-collaborative",
        "user-read-email",
        "user-read-private"
    };

    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>One HttpClient per process: per-request creation burned sockets
    /// (a paged sync of thousands of likes means dozens of connections in a row).</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SpotifyAuthStore _auth;

    /// <summary>Guard around auth-file writes: the UI thread and background syncs write it concurrently.</summary>
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public bool HasAuthFile => _auth.Exists;

    public bool HasAccessToken => !string.IsNullOrWhiteSpace(_auth.Load().AccessToken);

    /// <summary>Sync progress: (done, total).</summary>
    public event EventHandler<(int done, int total)>? SyncProgress;

    public SpotifyService(string authFilePath)
    {
        _auth = new SpotifyAuthStore(authFilePath);
    }

    // ============================ Client ID ==============================

    private static string ClientIdFilePath =>
        Path.Combine(App.AppDataDir, ClientIdFileName);

    private sealed class ClientIdFile
    {
        public string ClientId { get; set; } = string.Empty;
    }

    /// <summary>Whether a Client ID is configured (non-empty and not the legacy placeholder).</summary>
    public static bool IsClientIdConfigured
    {
        get
        {
            var id = LoadClientIdFile();
            return !string.IsNullOrWhiteSpace(id)
                && !id.Equals("YOUR_SPOTIFY_CLIENT_ID", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Current Client ID (empty string when not configured).</summary>
    public static string GetClientId() => LoadClientIdFile() ?? string.Empty;

    /// <summary>Saves the Client ID (spotify_client.json in AppData); trims surrounding spaces.</summary>
    public static void SaveClientId(string clientId)
    {
        clientId = (clientId ?? string.Empty).Trim();
        var dir = App.AppDataDir;
        Directory.CreateDirectory(dir);
        var path = ClientIdFilePath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new ClientIdFile { ClientId = clientId }, JsonOpts));
        File.Move(tmp, path, overwrite: true);
        _cachedClientId = clientId;
    }

    private static string? _cachedClientId;

    private static string? LoadClientIdFile()
    {
        if (_cachedClientId != null) return _cachedClientId;
        try
        {
            var path = ClientIdFilePath;
            if (!File.Exists(path))
            {
                _cachedClientId = string.Empty;
                return _cachedClientId;
            }
            var file = JsonSerializer.Deserialize<ClientIdFile>(File.ReadAllText(path), JsonOpts);
            _cachedClientId = file?.ClientId?.Trim() ?? string.Empty;
            return _cachedClientId;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify client id load failed");
            _cachedClientId = string.Empty;
            return _cachedClientId;
        }
    }

    // ============================== OAuth ===============================

    /// <summary>
    /// Generates the PKCE pair and the authorization URL.
    /// Throws InvalidOperationException when the Client ID is not configured —
    /// the caller (login window) must ask the user for it first.
    /// </summary>
    public static (string AuthUrl, string CodeVerifier) GenerateAuthUrl()
    {
        var clientId = GetClientId();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "Spotify Client ID is not configured. Enter it in the connect window first.");

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = Guid.NewGuid().ToString("N");

        var url = AccountsBase + "/authorize?client_id=" + Uri.EscapeDataString(clientId) +
                  "&response_type=code" +
                  "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                  "&code_challenge_method=S256" +
                  "&code_challenge=" + Uri.EscapeDataString(challenge) +
                  "&state=" + Uri.EscapeDataString(state) +
                  "&scope=" + Uri.EscapeDataString(string.Join(" ", Scopes));

        return (url, verifier);
    }

    public async Task<bool> ExchangeCodeForTokenAsync(string code, string codeVerifier, CancellationToken ct)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GetClientId(),
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = codeVerifier
            });

            using var response = await Http.PostAsync(AccountsBase + "/api/token", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error($"Spotify token exchange failed: {response.StatusCode}");
                return false;
            }

            var token = JsonSerializer.Deserialize<SpotifyTokenResponse>(json, JsonOpts);
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                return false;

            await SaveTokenAsync(token);

            // Profile (/v1/me): Settings shows "Connected as ..."; failure is not critical.
            try
            {
                await FetchAndStoreProfileAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Spotify profile fetch failed: {ex.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify token exchange failed");
            return false;
        }
    }

    private async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        var file = _auth.Load();
        if (string.IsNullOrEmpty(file.RefreshToken))
            return false;

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GetClientId(),
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = file.RefreshToken
            });

            using var response = await Http.PostAsync(AccountsBase + "/api/token", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error($"Spotify token refresh failed: {response.StatusCode}");
                return false;
            }

            var token = JsonSerializer.Deserialize<SpotifyTokenResponse>(json, JsonOpts);
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                return false;

            if (string.IsNullOrEmpty(token.RefreshToken))
                token.RefreshToken = file.RefreshToken;

            await SaveTokenAsync(token);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify token refresh failed");
            return false;
        }
    }

    private async Task SaveTokenAsync(SpotifyTokenResponse token)
    {
        await _authGate.WaitAsync();
        try
        {
            var file = _auth.Load();
            file.AccessToken = token.AccessToken;
            file.RefreshToken = token.RefreshToken ?? file.RefreshToken;
            file.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn).ToString("o");
            file.SavedAt = DateTime.UtcNow.ToString("o");
            _auth.Save(file);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task FetchAndStoreProfileAsync(CancellationToken ct)
    {
        var (_, body) = await SendApiAsync(ApiBase + "/me", ct);
        if (string.IsNullOrEmpty(body)) return;

        var profile = JsonSerializer.Deserialize<SpotifyUserProfile>(body, JsonOpts);
        if (profile == null || string.IsNullOrEmpty(profile.Id)) return;

        await _authGate.WaitAsync();
        try
        {
            var file = _auth.Load();
            file.UserId = profile.Id;
            file.DisplayName = profile.DisplayName ?? profile.Id;
            file.Email = profile.Email;
            _auth.Save(file);
        }
        finally
        {
            _authGate.Release();
        }
    }

    // ============================ Saved Tracks ==========================

    /// <summary>
    /// Syncs Liked Songs into the repository page by page;
    /// returns the number of synced tracks.
    /// </summary>
    public async Task<int> SyncSavedTracksAsync(SpotifyTracksRepository repository, CancellationToken ct)
    {
        var syncedAt = DateTime.UtcNow.ToString("o");
        var all = new List<SpotifyTrackRow>();
        var offset = 0;

        // Paginate by the response's Next link, not by page size: a short page
        // (items.Count < PageSize with Next still set) used to cut the sync in half.
        for (var iteration = 0; iteration < MaxSyncIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{ApiBase}/me/tracks?limit={PageSize}&offset={offset}";
            var response = await FetchSavedTracksPageAsync(url, ct);
            if (response == null || response.Items.Count == 0)
                break;

            var rows = ExtractTrackRows(response.Items, syncedAt);
            await repository.UpsertBatchAsync(rows);
            all.AddRange(rows);

            SyncProgress?.Invoke(this, (all.Count, response.Total));

            if (response.Next == null)
                break;

            // Trust the server-echoed offset only when it moved forward; otherwise
            // (echo == offset, or the API returned the same page) step ourselves —
            // offset strictly grows, so the loop cannot stall.
            offset = response.Offset > offset ? response.Offset : offset + PageSize;
        }

        SetLastSyncedUtc(DateTime.UtcNow);
        return all.Count;
    }

    /// <summary>Sync pagination guard: 10000 pages of 50 is larger than any library.</summary>
    private const int MaxSyncIterations = 10000;

    /// <summary>
    /// One /me/tracks page with a single retry: 429/5xx/network errors and an empty
    /// page with HTTP 200 are transient failures that used to cut the sync short.
    /// On a second failure: network exceptions propagate as is, bad statuses throw
    /// SpotifyApiException, a second empty page returns null (end of library).
    /// </summary>
    private async Task<SpotifySavedTracksResponse?> FetchSavedTracksPageAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            Exception? failure = null;
            var status = 0;
            SpotifySavedTracksResponse? response = null;
            try
            {
                var (st, json) = await SendApiAsync(url, ct);
                status = st;
                if (st == 200)
                    response = JsonSerializer.Deserialize<SpotifySavedTracksResponse>(json, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (response != null && response.Items.Count > 0)
                return response;

            if (attempt == 1)
            {
                Logger.Warn($"Spotify /me/tracks page failed (status={status}, items={response?.Items.Count ?? -1}) — retrying once");
                await Task.Delay(750, ct);
                continue;
            }

            if (failure != null) throw failure;
            if (status != 200)
                throw new SpotifyApiException($"Spotify API failed with HTTP {status}", status);
            return response;
        }
    }

    private static List<SpotifyTrackRow> ExtractTrackRows(List<SpotifySavedTrackItem> items, string syncedAt)
    {
        var rows = new List<SpotifyTrackRow>(items.Count);
        foreach (var item in items)
        {
            var track = item.Track;
            // is_local = Spotify's "local files": no Id, unplayable via the Web API —
            // skip without breaking the numbering/count.
            if (track == null || string.IsNullOrEmpty(track.Id) || track.IsLocal)
                continue;

            var artist = (track.Artists != null && track.Artists.Count > 0)
                ? string.Join(", ", track.Artists.Select(a => a.Name))
                : string.Empty;
            var artworkUrl = track.Album?.Images?.FirstOrDefault()?.Url ?? string.Empty;

            rows.Add(new SpotifyTrackRow
            {
                SpotifyId = track.Id,
                Title = track.Name ?? string.Empty,
                Artist = artist,
                Album = track.Album?.Name ?? string.Empty,
                DurationMs = track.DurationMs,
                ArtworkUrl = artworkUrl,
                IsPlayable = track.IsPlayable ?? true,
                AddedAt = item.AddedAt ?? syncedAt,
                SyncedAt = syncedAt
            });
        }
        return rows;
    }

    private async Task<(int Status, string Body)> SendApiAsync(string url, CancellationToken ct)
    {
        var token = await EnsureValidTokenAsync(ct);
        if (token == null)
            throw new SpotifyApiException("No valid access token", 401);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, body);
    }

    private async Task<string?> EnsureValidTokenAsync(CancellationToken ct)
    {
        var file = _auth.Load();
        if (!string.IsNullOrEmpty(file.ExpiresAt)
            && DateTime.TryParse(file.ExpiresAt, null, DateTimeStyles.RoundtripKind, out var expiresAt)
            && expiresAt < DateTime.UtcNow.AddMinutes(5))
        {
            if (!await RefreshTokenAsync(ct))
                return null;
            file = _auth.Load();
        }
        return file.AccessToken;
    }

    // ============================= Account ==============================

    public void Disconnect()
    {
        _auth.Delete();
    }

    public DateTime? GetLastSyncedUtc()
    {
        if (!DateTime.TryParse(_auth.Load().LastSyncedAtUtc, null, DateTimeStyles.RoundtripKind, out var value))
            return null;
        return value;
    }

    public void SetLastSyncedUtc(DateTime utc)
    {
        _authGate.Wait();
        try
        {
            var file = _auth.Load();
            file.LastSyncedAtUtc = utc.ToString("o");
            _auth.Save(file);
        }
        finally
        {
            _authGate.Release();
        }
    }

    public string GetUserId()
    {
        return _auth.Load().UserId ?? string.Empty;
    }

    public string GetDisplayName()
    {
        return _auth.Load().DisplayName ?? string.Empty;
    }

    // =============================== PKCE ===============================

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha = SHA256.Create();
        var challenge = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier)));
        return challenge.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
