using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatPlayer.Helpers;

public static class FileHelpers
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".opus", ".aac", ".m4a", ".wma", ".alac", ".aiff", ".aif"
    };

    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path);
        return AudioExtensions.Contains(ext);
    }

    public static IEnumerable<string> EnumerateAudioFiles(string folder, bool recursive = true)
    {
        if (!Directory.Exists(folder)) return Enumerable.Empty<string>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", option)
                            .Where(IsAudioFile);
        }
        catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        catch (DirectoryNotFoundException) { return Enumerable.Empty<string>(); }
    }

    public static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    public static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    public static string FormatBitrate(int kbps) => kbps > 0 ? $"{kbps} kbps" : "—";
}
