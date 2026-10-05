using System;
using System.IO;
using System.Text.Json;
using BatPlayer.Services;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Содержимое vk_auth.json: cookies веб-сессии vk.com/vk.ru (единый принцип с
/// SoundCloud-интеграцией) и id пользователя VK. AccessToken — историческое поле
/// эпохи OAuth/Kate Mobile: токены audio.get мертвы (коды 3/8), поле оставлено
/// для совместимости старых файлов, но не используется и не обязательно.
/// ФАЙЛ СОДЕРЖИТ COOKIES СЕССИИ — в логи не печатать, хранится только локально у пользователя.
/// </summary>
public sealed class VkAuthFile
{
    /// <summary>Cookie-строка "name=value; name=value" для vk.com/vk.ru (собирается окном входа).</summary>
    public string? CookieHeader { get; set; }
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    /// <summary>Момент сохранения токена (после входа).</summary>
    public string? SavedAt { get; set; }
    /// <summary>Дата последней успешной синхронизации библиотеки (ISO, roundtrip).</summary>
    public string? LastSyncedAtUtc { get; set; }
}

/// <summary>
/// Загрузка/сохранение/удаление vk_auth.json (%LOCALAPPDATA%/BatPlayer).
/// Atomic write, как в SoundCloudAuthStore/SettingsService.
/// </summary>
public sealed class VkAuthService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public VkAuthService(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public VkAuthFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new VkAuthFile();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<VkAuthFile>(json, JsonOpts) ?? new VkAuthFile();
        }
        catch (Exception ex)
        {
            // Содержимое файла (токен) в сообщение исключения не попадает — путь и тип ошибки.
            Logger.Error(ex, "VK auth load failed");
            return new VkAuthFile();
        }
    }

    public void Save(VkAuthFile file)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new System.Text.UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Удаляет файл авторизации (Disconnect / отозванный токен).</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK auth delete failed");
        }
    }
}
