using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BatPlayer.Services.Spotify;

/// <summary>
/// Атомарное чтение/запись spotify_auth.json (токены + профиль + метка последнего синка).
/// Запись через .tmp + Move(overwrite) — обрыв питания не оставит битый JSON.
/// </summary>
public sealed class SpotifyAuthStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public bool Exists => File.Exists(_path);

    public SpotifyAuthStore(string path)
    {
        _path = path;
    }

    public SpotifyAuthFile Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new SpotifyAuthFile();

            return JsonSerializer.Deserialize<SpotifyAuthFile>(File.ReadAllText(_path), JsonOpts)
                   ?? new SpotifyAuthFile();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify auth load failed");
            return new SpotifyAuthFile();
        }
    }

    public void Save(SpotifyAuthFile file)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(JsonSerializer.Serialize(file, JsonOpts));
        }
        File.Move(tmp, _path, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify auth delete failed");
        }
    }
}
