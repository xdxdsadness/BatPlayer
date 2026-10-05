using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// Логгер в файл без блокировки вызывающего потока.
/// Путь: %LocalAppData%/BatPlayer/logs/app_YYYYMMDD.log
/// Вызывающий поток (в т.ч. UI) только кладёт строку в ограниченную очередь —
/// диск пишет фоновый поток батчами. Раньше каждый вызов делал синхронный
/// AppendAllText под глобальным локом: на тиках позиции это давало постоянные
/// микроподвисания UI и лог 400+ МБ/день.
/// Ротация: по дню; файл > 64 МБ при открытии усекается; логи старше 7 дней
/// удаляются на старте.
/// </summary>
public static class Logger
{
    private const int MaxQueue = 16384;      // переполнение — строки теряются, UI не ждёт
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
            // TryAdd: при переполнении отбрасываем строку вместо блокировки автора.
            Queue.TryAdd(line);
        }
        catch { /* очередь закрыта при выгрузке — писать некуда */ }
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
                // Батчинг: всплеск строк копится в буфере, сбрасываем когда очередь пуста.
                if (Queue.Count == 0)
                    writer.Flush();
            }
        }
        catch { /* логгер не должен ронять приложение */ }
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
                    File.Delete(path); // раздувшийся файл дня усекаем, начинаем заново
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
