using System;
using System.IO;
using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Содержимое ym_auth.json: OAuth-токен Яндекс ID (неявный поток response_type=token с
/// официальным client_id Яндекс Музыки) и данные аккаунта. Единый принцип с
/// SoundCloud/VK-интеграциями. CookieHeader — историческое поле эпохи cookies-сессии:
/// api.music.yandex.net отвечает на Session_id-cookies 401, поле оставлено для
/// совместимости старых файлов, всегда пустое. ФАЙЛ СОДЕРЖИТ ТОКЕН ДОСТУПА — в логи не
/// печатать, хранится только локально у пользователя.
/// </summary>
public sealed class YmAuthFile
{
    /// <summary>OAuth-токен Яндекс ID (из fragment "#access_token=…" ответа oauth.yandex.ru).</summary>
    public string? AccessToken { get; set; }

    /// <summary>Историческое поле cookies веб-сессии .yandex.ru: API его не принимает,
    /// поле не используется и остаётся пустым (совместимость чтения старых файлов).</summary>
    public string? CookieHeader { get; set; }

    /// <summary>uid аккаунта Яндекс Музыки (из account/status при входе); нужен для likes/tracks.</summary>
    public string? Uid { get; set; }

    /// <summary>Отображаемое имя аккаунта (для статуса в настройках); null — не определено.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Момент сохранения сессии (ISO-8601, UtcNow) — для статуса в настройках.</summary>
    public string? SavedAt { get; set; }

    /// <summary>Дата последней успешной синхронизации каталога (ISO-8601, UtcNow).</summary>
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>
/// Загрузка/сохранение/удаление ym_auth.json (%LOCALAPPDATA%/BatPlayer).
/// Atomic write, как в SettingsService.
/// </summary>
public sealed class YmAuthService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public YmAuthService(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public YmAuthFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new YmAuthFile();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<YmAuthFile>(json, JsonOpts) ?? new YmAuthFile();
        }
        catch (Exception ex)
        {
            // Содержимое файла (токен) в сообщение исключения не попадает — путь и тип ошибки.
            Logger.Error(ex, "Yandex Music auth load failed");
            return new YmAuthFile();
        }
    }

    public void Save(YmAuthFile file)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new System.Text.UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Удаляет файл авторизации (Disconnect). Токен после этого недоступен.</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music auth delete failed");
        }
    }
}
