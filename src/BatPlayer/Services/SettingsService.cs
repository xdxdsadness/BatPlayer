using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Загрузка/сохранение AppSettings в JSON. Single-file, atomic write.
/// </summary>
public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Allow NaN / Infinity values (WindowLeft/Top default to NaN when window not yet shown)
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public AppSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    // Update() fires SaveAsync without awaiting; serialize saves so two
    // concurrent writers can't consume each other's tmp file.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsService(string path)
    {
        _path = path;
        Current = Load() ?? new AppSettings();
    }

    public AppSettings? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Settings load failed");
            return null;
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await _saveLock.WaitAsync();
            try
            {
                var tmp = _path + ".tmp";
                await using (var fs = File.Create(tmp))
                    await JsonSerializer.SerializeAsync(fs, Current, JsonOpts);

                // Atomic replace that works whether destination exists or not
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                _saveLock.Release();
            }

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Settings save failed");
        }
    }

    public void Update(Action<AppSettings> mutator)
    {
        mutator(Current);
        _ = SaveAsync();
    }
}
