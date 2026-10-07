using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BatPlayer.Services;

/// <summary>
/// Atomic JSON file store for platform credentials (auth tokens, session cookies).
/// Writes go through a .tmp file + Move(overwrite), so a power cut never leaves broken
/// JSON. File contents hold access tokens — never logged.
/// </summary>
public abstract class JsonAuthStore<TFile> where TFile : class, new()
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _logName;

    protected JsonAuthStore(string path, string logName)
    {
        _path = path;
        _logName = logName;
    }

    public bool Exists => File.Exists(_path);

    public TFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new TFile();
            return JsonSerializer.Deserialize<TFile>(File.ReadAllText(_path), JsonOpts) ?? new TFile();
        }
        catch (Exception ex)
        {
            // File contents (tokens) never appear in the exception message — path and error type only.
            Logger.Error(ex, $"{_logName} auth load failed");
            return new TFile();
        }
    }

    public void Save(TFile file)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            writer.Write(JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Deletes the auth file (disconnect); credentials become unavailable.</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{_logName} auth delete failed");
        }
    }
}
