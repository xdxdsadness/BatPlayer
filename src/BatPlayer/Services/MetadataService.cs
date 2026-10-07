using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BatPlayer.Models;
using TagLib;

namespace BatPlayer.Services;

/// <summary>
/// Metadata reading via TagLib#. Extracts tags, cover art and technical parameters.
/// </summary>
public sealed class MetadataService
{
    public Task<Track?> ReadAsync(string filePath)
    {
        return Task.Run(() =>
        {
            try
            {
                var tFile = TagLib.File.Create(filePath);
                var props = tFile.Properties;
                var tags  = tFile.Tag;

                var fi = new FileInfo(filePath);
                var track = new Track
                {
                    FilePath      = filePath,
                    Title         = string.IsNullOrWhiteSpace(tags.Title) ? Path.GetFileNameWithoutExtension(filePath) : tags.Title,
                    Artist        = string.IsNullOrWhiteSpace(tags.FirstPerformer) ? "Неизвестный исполнитель" : tags.FirstPerformer,
                    Album         = string.IsNullOrWhiteSpace(tags.Album) ? "Неизвестный альбом" : tags.Album,
                    Genre         = tags.FirstGenre ?? string.Empty,
                    Year          = (int)tags.Year,
                    TrackNumber   = (int)tags.Track,
                    DurationTicks = props.Duration.Ticks,
                    Bitrate       = props.AudioBitrate,
                    SampleRate    = props.AudioSampleRate,
                    Channels      = props.AudioChannels,
                    Format        = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
                    FileSizeBytes = fi.Length,
                    DateAdded     = DateTime.UtcNow,
                    IsAvailable   = true
                };

                // Cover hash for dedup
                if (tags.Pictures?.Length > 0)
                {
                    var pic = tags.Pictures[0];
                    track.CoverHash = ComputeHash(pic.Data.Data);
                }

                tFile.Dispose();
                return track;
            }
            catch (UnsupportedFormatException)
            {
                Logger.Warn($"Unsupported format: {filePath}");
                return null;
            }
            catch (CorruptFileException)
            {
                Logger.Warn($"Corrupt file: {filePath}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Metadata read failed: {filePath}");
                return null;
            }
        });
    }

    public byte[]? ExtractCoverBytes(string filePath)
    {
        try
        {
            using var tFile = TagLib.File.Create(filePath);
            return tFile.Tag.Pictures?.FirstOrDefault()?.Data.Data;
        }
        catch { return null; }
    }

    private static string ComputeHash(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
