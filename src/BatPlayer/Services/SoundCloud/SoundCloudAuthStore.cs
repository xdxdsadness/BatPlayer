using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Содержимое sc_auth.json: cookies веб-сессии SoundCloud и последний валидный client_id.
/// ФАЙЛ СОДЕРЖИТ ТОКЕН ДОСТУПА — в логи не печатать, хранится только локально у пользователя.
/// </summary>
public sealed class SoundCloudAuthFile
{
    public string? Cookies { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Id пользователя (GET /me, поле id) — кэш для /users/{id}/likes, чтобы не дёргать /me.</summary>
    public string? UserId { get; set; }
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>
/// Загрузка/сохранение/удаление sc_auth.json (%LOCALAPPDATA%/BatPlayer).
/// Atomic write, как в SettingsService.
/// </summary>
public sealed class SoundCloudAuthStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public SoundCloudAuthStore(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public SoundCloudAuthFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SoundCloudAuthFile();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SoundCloudAuthFile>(json, JsonOpts) ?? new SoundCloudAuthFile();
        }
        catch (Exception ex)
        {
            // Содержимое файла (cookies) в сообщение исключения не попадает — путь и тип ошибки.
            Logger.Error(ex, "SoundCloud auth load failed");
            return new SoundCloudAuthFile();
        }
    }

    public void Save(SoundCloudAuthFile file)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new System.Text.UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Удаляет файл авторизации (Disconnect). Cookies после этого недоступны.</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud auth delete failed");
        }
    }
}
