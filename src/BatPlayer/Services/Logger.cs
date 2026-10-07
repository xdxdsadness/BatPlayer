using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// File logger that never blocks the calling thread.
/// Path: %LocalAppData%/BatPlayer/logs/app_YYYYMMDD.log
/// The calling thread (including UI) only enqueues the line into a bounded queue —
/// a background thread writes to disk in batches. Each call used to do a synchronous
/// AppendAllText under a global lock: on position ticks that meant constant UI
/// micro-stalls and a 400+ MB/day log.
/// Rotation: daily; a file > 64 MB is truncated on open; logs older than 7 days
/// are deleted at startup.
/// </summary>
public static class Logger
{
    private const int MaxQueue = 16384;      // on overflow lines are dropped — the UI never waits
    private const long MaxLogFileBytes = 64 * 1024 * 1024;
    private const int RetentionDays = 7;

    private static readonly string LogDir = Path.Combine(App.AppDataDir, "logs");
    private static readonly BlockingCollection<string> Queue = new(MaxQueue);

    static Logger()
    {
        var writer = new Thread(WriterLoop) { IsBackground = true, Name = "logger" };
        writer.Start();
        Task.Run(CleanupOldLogs);
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(Exception ex, string context = "") => Write("ERROR", $"{context}: {ex}");
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        try
        {
            // TryAdd: on overflow the line is dropped instead of blocking the caller.
            Queue.TryAdd(line);
        }
        catch { /* queue closed at shutdown — nowhere to write */ }
    }

    private static void WriterLoop()
    {
        var currentDay = string.Empty;
        StreamWriter? writer = null;
        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                var day = DateTime.Now.ToString("yyyyMMdd");
                if (writer == null || day != currentDay)
                {
                    writer?.Dispose();
                    writer = Open(day);
                    currentDay = day;
                }

                writer.WriteLine(line);
                // Batching: a burst of lines accumulates in the buffer; flush when the queue is empty.
                if (Queue.Count == 0)
                    writer.Flush();
            }
        }
        catch { /* the logger must never crash the app */ }
        finally
        {
            try { writer?.Flush(); writer?.Dispose(); } catch { }
        }
    }

    private static StreamWriter Open(string day)
    {
        Directory.CreateDirectory(LogDir);
        var path = Path.Combine(LogDir, $"app_{day}.log");
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > MaxLogFileBytes)
                    File.Delete(path); // truncate the bloated day file and start over
            }
            catch { }
        }
        return new StreamWriter(path, append: true);
    }

    private static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDir, "app_*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }
}
