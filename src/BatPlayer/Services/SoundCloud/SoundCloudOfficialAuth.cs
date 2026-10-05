using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatPlayer.Services;

namespace BatPlayer.Services.SoundCloud;

/// <summary>Содержимое sc_api_auth.json: токен официального SoundCloud API
/// (OAuth 2.1, локальный callback-флоу) и client_id зарегистрированного приложения.
/// ФАЙЛ СОДЕРЖИТ ТОКЕН — в логи не печатать.</summary>
public sealed class SoundCloudApiAuthFile
{
    public string? AccessToken { get; set; }
    /// <summary>Refresh-токен OAuth 2.1: без него access_token после истечения (~час)
    /// не восстановить — стримы официального API массово отвечают 401 («не играют»).</summary>
    public string? RefreshToken { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>Когда истекает access_token (UTC); null — неизвестно (файлы старых версий).</summary>
    public DateTime? AccessTokenExpiresAtUtc { get; set; }
    public string? SavedAtUtc { get; set; }
}

/// <summary>Хранилище sc_api_auth.json (%LOCALAPPDATA%/BatPlayer). Atomic write.</summary>
public sealed class SoundCloudApiAuthStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;

    public SoundCloudApiAuthStore(string path) => _path = path;

    public SoundCloudApiAuthFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SoundCloudApiAuthFile();
            return JsonSerializer.Deserialize<SoundCloudApiAuthFile>(File.ReadAllText(_path))
                   ?? new SoundCloudApiAuthFile();
        }
        catch
        {
            return new SoundCloudApiAuthFile();
        }
    }

    public async Task SaveAsync(SoundCloudApiAuthFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(_path); } catch { /* нет файла */ }
    }
}

/// <summary>
/// Официальный SoundCloud API: подключение через OAuth 2.1 authorization-code +
/// PKCE с ЛОКАЛЬНЫМ callback-сервером (http://127.0.0.1:8765/callback — документированный
/// десктоп-флоу официального CLI). После токена — авто-регистрация приложения
/// (POST api-reg.soundcloud.com/me/apps) для клиентских учётных данных.
/// </summary>
public sealed class SoundCloudOfficialAuth
{
    private const string AuthorizeBase = "https://secure.soundcloud.com/authorize";
    private const string TokenUrl = "https://secure.soundcloud.com/oauth/token";
    private const string AppRegBase = "https://api-reg.soundcloud.com";
    /// <summary>Публичный PKCE-клиент CLI официального репозитория.</summary>
    private const string BundledClientId = "nXIZT4VQQYkgHs75vpIYbnINQciCkV5Y";
    private const string CallbackUri = "http://127.0.0.1:8765/callback";
    private const int CallbackPort = 8765;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SoundCloudApiAuthStore _store;

    public SoundCloudOfficialAuth(SoundCloudApiAuthStore store) => _store = store;

    public void Disconnect() => _store.Delete();

    public SoundCloudApiAuthFile Load() => _store.Load();
    public bool IsConnected => !string.IsNullOrEmpty(_store.Load().AccessToken);

    /// <summary>Локальное подключение (совместимость): полный официальный флоу —
    /// PKCE-вход → регистрация/получение СВОЕГО приложения → переподключение своим клиентом.
    /// onStatus — тексты прогресса для UI.</summary>
    public Task<SoundCloudApiAuthFile> ConnectLocallyAsync(
        Action<string>? onStatus, CancellationToken ct)
        => ConnectOfficialAsync(onStatus, ct);

    /// <summary>
    /// Полное подключение официального API — 1:1 с официальным CLI sc-api-auth.mjs:
    /// 1) PKCE-вход в браузере: СВОЙ client_id (если уже зарегистрирован) или бандлед;
    /// 2) POST /me/apps — регистрация собственного приложения (или получение уже
    ///    существующего), схема тела и обработка ошибок — как в CLI;
    /// 3) если токен выдан бандлед-клиентом, а зарегистрирован СВОЙ — второй PKCE-проход:
    ///    api.soundcloud.com отвечает 403 disallowed на токены заблокированного
    ///    бандлед-клиента, и только токен собственного приложения может играть.
    /// </summary>
    public async Task<SoundCloudApiAuthFile> ConnectOfficialAsync(
        Action<string>? onStatus, CancellationToken ct)
    {
        var saved = _store.Load();
        var loginClient = string.IsNullOrWhiteSpace(saved.ClientId) ? BundledClientId : saved.ClientId!;
        var file = await ConnectWithClientIdAsync(loginClient, saved.ClientSecret, onStatus, ct);

        onStatus?.Invoke("Регистрирую приложение SoundCloud API...");
        var creds = await EnsureRegisteredAppAsync(file.AccessToken!, onStatus, ct);
        if (creds == null || string.IsNullOrEmpty(creds.Value.ClientId) || creds.Value.ClientId == loginClient)
            return file;

        onStatus?.Invoke("Переподключаюсь с учётными данными вашего приложения (второй вход в браузере)...");
        return await ConnectWithClientIdAsync(creds.Value.ClientId, creds.Value.ClientSecret, onStatus, ct);
    }

    /// <summary>PKCE-проход с указанным клиентом: браузер → callback → токен →
    /// регистрация приложения (если client_secret).</summary>
    public async Task<SoundCloudApiAuthFile> ConnectWithClientIdAsync(
        string clientId, string? clientSecret, Action<string>? onStatus, CancellationToken ct)
    {
        var (verifier, challenge, state) = NewPkce();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}/callback/");
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/callback/");
        listener.Start();
        onStatus?.Invoke("Ожидаю вход в браузере SoundCloud...");

        var authUrl = $"{AuthorizeBase}?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(CallbackUri)}" +
                      $"&response_type=code&code_challenge={challenge}" +
                      $"&code_challenge_method=S256&state={state}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = authUrl, UseShellExecute = true });
        }
        catch { /* браузер не открылся — пользователь откроет ссылку вручную (статус выше) */ }

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

        // Ждём callback: первый запрос на /callback с code и state.
        string? code = null;
        while (code == null && !ct.IsCancellationRequested)
        {
            var ctx = await listener.GetContextAsync();
if (ct.IsCancellationRequested) { try { listener.Stop(); } catch { } throw new OperationCanceledException(ct); }
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
            {
                Reply(ctx, 404, "Not found");
                continue;
            }
            var query = ctx.Request.QueryString;
            var callbackError = query["error"];
            code = query["code"];
            var returnedState = query["state"];

            if (!string.IsNullOrEmpty(callbackError))
            {
                Reply(ctx, 200, "<html><body style='font-family:Segoe UI;background:#1a1d22;color:#f1f1f1'>Отменено: " +
                    System.Net.WebUtility.HtmlEncode(callbackError) + "</body></html>");
                throw new Exception("Вход отменён: " + callbackError);
            }
            if (string.IsNullOrEmpty(code))
            {
                Reply(ctx, 400, "Missing code");
                continue; // не callback с кодом — ждём дальше
            }
            if (returnedState != state)
            {
                Reply(ctx, 400, "Invalid state");
                throw new Exception("State mismatch (CSRF)");
            }
            Reply(ctx, 200, "<html><body style='font-family:Segoe UI;background:#1a1d22;color:#f1f1f1;text-align:center;padding-top:40px'>" +
                "<h2>SoundCloud API подключён</h2>Можно закрыть это окно.</body></html>");
        }
        ct.ThrowIfCancellationRequested();

        onStatus?.Invoke("Обмениваю код на токен...");
        var token = await TokenExchangeAsync(code!, verifier, clientId, ct);

        var file = new SoundCloudApiAuthFile
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessTokenExpiresAtUtc = token.ExpiresAtUtc,
            SavedAtUtc = DateTime.UtcNow.ToString("o")
        };
        await _store.SaveAsync(file);
        return file;
    }

    // ====================== Жизненный цикл токена ====================

    /// <summary>Запас до истечения access_token, при котором считаем его протухшим.</summary>
    private static readonly TimeSpan TokenExpiryMargin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Живой access_token для запросов официального API. Истекающий/протухший (или
    /// forceRefresh после 401) обновляется refresh-грантом; refresh-токен ротируется
    /// ответом и перезаписывается в файл. null — токена нет; если обновить не удалось,
    /// отдаём прежний (запрос API всё равно вернёт 401 — но не молча теряем сессию).
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var file = _store.Load();
        if (string.IsNullOrEmpty(file.AccessToken)) return null;

        var expired = file.AccessTokenExpiresAtUtc.HasValue
                      && DateTime.UtcNow >= file.AccessTokenExpiresAtUtc.Value - TokenExpiryMargin;
        if (!expired && !forceRefresh) return file.AccessToken;
        if (string.IsNullOrEmpty(file.RefreshToken)) return file.AccessToken; // старый файл без refresh — как раньше

        await RefreshAsync(file, ct);
        return _store.Load().AccessToken;
    }

    /// <summary>POST /oauth/token (grant_type=refresh_token); новые токены — в файл.</summary>
    private async Task RefreshAsync(SoundCloudApiAuthFile file, CancellationToken ct)
    {
        try
        {
            var (status, body) = await SoundCloudHttp.SendWithFailoverAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = file.ClientId ?? BundledClientId,
                        ["refresh_token"] = file.RefreshToken!
                    })
                };
                return req;
            }, ct);
            if (status != 200)
            {
                Logger.Warn($"SoundCloud API token refresh failed with HTTP {status}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access))
            {
                Logger.Warn("SoundCloud API token refresh response has no access_token");
                return;
            }

            file.AccessToken = access;
            // OAuth 2.1 ротация: старый refresh-токен инвалидируется, сохраняем новый.
            if (root.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()))
                file.RefreshToken = rt.GetString();
            if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number)
                file.AccessTokenExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(ei.GetDouble());
            file.SavedAtUtc = DateTime.UtcNow.ToString("o");
            await _store.SaveAsync(file);
            Logger.Info("SoundCloud API access token refreshed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Обновление не должно ронять воспроизведение: вернётся прежний токен.
            Logger.Error(ex, "SoundCloud API token refresh failed");
        }
    }

    private static void Reply(HttpListenerContext ctx, int status, string html)
    {
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { /* клиент уже ушёл */ }
    }

    /// <summary>PKCE: verifier + S256 challenge + state.</summary>
    private static (string verifier, string challenge, string state) NewPkce()
    {
        var verifier = Base64Url(RandomBytes(32));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var state = Base64Url(RandomBytes(24));
        return (verifier, challenge, state);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Base64Url(byte[] buf)
        => Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>POST /oauth/token (grant_type=authorization_code + PKCE verifier).</summary>
    private static async Task<(string AccessToken, string? RefreshToken, DateTime? ExpiresAtUtc)> TokenExchangeAsync(
        string code, string verifier, string clientId, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = CallbackUri,
            ["code_verifier"] = verifier,
            ["code"] = code
        });
        using var res = await Http.PostAsync(TokenUrl, body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed: {(int)res.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var at)
            ? at.GetString() ?? throw new Exception("No access_token in token response")
            : throw new Exception("No access_token in token response");
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTime? expiresAt = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? DateTime.UtcNow + TimeSpan.FromSeconds(ei.GetDouble())
            : null;
        return (access, refresh, expiresAt);
    }

    // ====================== Регистрация приложения (1:1 с CLI) ====================

    /// <summary>Метаданные регистрируемого приложения. POST /me/apps требует
    /// name+description+website (как CLI sc-api-auth.mjs).</summary>
    private const string AppName = "Bat Player";
    private const string AppDescription = "Personal desktop music player: plays the user's own SoundCloud likes and library via the official API";
    private const string AppWebsite = "https://github.com/danil";

    /// <summary>
    /// Регистрация (или получение уже существующего) собственного приложения —
    /// по схеме официального CLI sc-api-auth.mjs. Возвращает учётные данные или
    /// null, если регистрация недоступна (например, нужна подписка Artist Pro —
    /// код application_creation_not_available; текст для UI — в onStatus).
    /// </summary>
    private async Task<(string? ClientId, string? ClientSecret)?> EnsureRegisteredAppAsync(
        string accessToken, Action<string>? onStatus, CancellationToken ct)
    {
        // 1) Создание: POST /me/apps {name, description, website} — без обёрток.
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{AppRegBase}/me/apps"))
        {
            req.Headers.Authorization = new("OAuth", accessToken);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = AppName,
                description = AppDescription,
                website = AppWebsite
            }), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                var (cid, secret, urn) = ParseCredentials(text);
                // Новому приложению нужен redirect_uri для будущих PKCE-входов
                // (CLI ставит его отдельным PUT /me/apps/{credentialsUrn}).
                if (!string.IsNullOrEmpty(urn))
                    await UpdateRedirectAsync(accessToken, urn, ct);
                onStatus?.Invoke($"Приложение зарегистрировано (client_id {cid}).");
                return (cid, secret);
            }

            var (code, apiMsg) = ParseApiError(text);
            if (code != "user_already_has_application")
            {
                Logger.Warn($"SoundCloud app registration failed: HTTP {(int)res.StatusCode} code={code} {apiMsg}");
                onStatus?.Invoke(RegistrationHint(code, apiMsg));
                return null;
            }
        }

        // 2) Приложение уже есть: GET /me/apps → первое → PUT redirect (в ответе —
        //    полные учётные данные, GET client_secret не отдаёт).
        onStatus?.Invoke("Приложение уже зарегистрировано — получаю его учётные данные...");
        using (var get = new HttpRequestMessage(HttpMethod.Get, $"{AppRegBase}/me/apps"))
        {
            get.Headers.Authorization = new("OAuth", accessToken);
            using var res = await Http.SendAsync(get, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Logger.Warn($"SoundCloud list apps failed: HTTP {(int)res.StatusCode} {text}");
                return null;
            }
            var (cid, secret, urn) = ParseFirstApp(text);
            if (cid == null)
            {
                Logger.Warn("SoundCloud list apps: no application with client_id");
                return null;
            }

            if (urn != null)
            {
                var (_, secret2) = await UpdateRedirectAsync(accessToken, urn, ct);
                if (!string.IsNullOrEmpty(secret2)) secret = secret2;
            }
            onStatus?.Invoke($"Найдено зарегистрированное приложение (client_id {cid}).");
            return (cid, secret);
        }
    }

    /// <summary>PUT /me/apps/{credentialsUrn} {redirect_uri} — ставит локальный callback
    /// для будущих PKCE-входов. В ответе — полные учётные данные (включая secret).</summary>
    private async Task<(bool Ok, string? ClientSecret)> UpdateRedirectAsync(
        string accessToken, string urn, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{AppRegBase}/me/apps/{urn}");
        req.Headers.Authorization = new("OAuth", accessToken);
        req.Content = new StringContent(JsonSerializer.Serialize(new { redirect_uri = CallbackUri }),
            Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var (code, msg) = ParseApiError(text);
            Logger.Warn($"SoundCloud redirect update failed: HTTP {(int)res.StatusCode} code={code} {msg}");
            return (false, null);
        }
        var (_, secret, _) = ParseCredentials(text);
        return (true, secret);
    }

    private static (string? ClientId, string? ClientSecret, string? Urn) ParseCredentials(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string n)
                => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("client_id"), Get("client_secret"), Get("credentials"));
        }
        catch { return (null, null, null); }
    }

    private static (string? ClientId, string? ClientSecret, string? Urn) ParseFirstApp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("collection", out var col)) return (null, null, null);
            foreach (var app in col.EnumerateArray())
            {
                if (!app.TryGetProperty("client_id", out var cid) || cid.ValueKind != JsonValueKind.String)
                    continue;
                string? Get(string n)
                    => app.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return (cid.GetString(), Get("client_secret"), Get("credentials"));
            }
        }
        catch { }
        return (null, null, null);
    }

    /// <summary>Разбор errors[0].code / error_message (структура api-reg), fallback {"error": "..."}.</summary>
    private static (string? Code, string? Message) ParseApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0)
            {
                var first = errs[0];
                string? code = first.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                string? msg = first.TryGetProperty("error_message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                return (code, msg);
            }
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return (e.GetString(), null);
        }
        catch { }
        return (null, null);
    }

    /// <summary>Текст для UI по коду ошибки регистрации (как CREATE_APP_ERROR_MESSAGES в CLI).</summary>
    private static string RegistrationHint(string? code, string? apiMsg)
    {
        if (!string.IsNullOrEmpty(apiMsg)) return apiMsg;
        return code switch
        {
            "application_creation_not_available"
                => "Регистрация приложений недоступна для аккаунта — нужна подписка SoundCloud Artist Pro",
            "application_name_not_allowed" => "Имя приложения отклонено SoundCloud",
            "application_website_not_allowed" => "URL сайта приложения отклонён SoundCloud",
            _ => $"Регистрация приложения недоступна ({code ?? "unknown"})"
        };
    }
}
