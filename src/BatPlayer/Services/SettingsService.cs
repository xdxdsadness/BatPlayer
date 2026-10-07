using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Loads/saves AppSettings as JSON. Single-file, atomic write.
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
            var json = MigrateLegacyKeys(File.ReadAllText(_path));
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Settings load failed");
            return null;
        }
    }

    /// <summary>Renames keys from older app versions to their current names.</summary>
    private static string MigrateLegacyKeys(string json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject obj
                || obj.ContainsKey("dpiBypassEnabled")
                || obj["soundCloudZapretEnabled"] is not { } legacy)
                return json;

            obj["dpiBypassEnabled"] = legacy.DeepClone();
            obj.Remove("soundCloudZapretEnabled");
            return obj.ToJsonString();
        }
        catch
        {
            return json; // broken json is handled by Deserialize
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
