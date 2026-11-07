using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Tiny thread-safe file logger. Writes to %LocalAppData%\WindowsDynamicIsland\logs\app.log
/// on a background thread; rotates when the file exceeds 1 MB. Never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 2048);
    private static readonly string LogDir = Path.Combine(AppPaths.LocalDataDir, "logs");
    private static readonly string LogFile = Path.Combine(LogDir, "app.log");
    private static Thread? _writer;
    private static readonly object InitGate = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    [Conditional("DEBUG")]
    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            System.Diagnostics.Debug.WriteLine(line);
            EnsureWriter();
            Queue.TryAdd(line);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (InitGate)
        {
            if (_writer is not null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "LogWriter", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        try { Directory.CreateDirectory(LogDir); } catch { return; }

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = LogFile + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogFile, old);
                }
                var sb = new StringBuilder(line).AppendLine();
                // Drain whatever else is queued to batch disk writes.
                while (Queue.TryTake(out var next)) sb.AppendLine(next);
                File.AppendAllText(LogFile, sb.ToString());
            }
            catch
            {
                // ignore IO errors
            }
        }
    }

    /// <summary>Flushes pending lines (best effort, bounded wait). Call on shutdown.</summary>
    public static void Flush(int timeoutMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
    }
}

public static class AppPaths
{
    public const string AppFolderName = "WindowsDynamicIsland";

    /// <summary>%AppData%\WindowsDynamicIsland — roaming settings.</summary>
    public static string RoamingDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>%LocalAppData%\WindowsDynamicIsland — logs, caches.</summary>
    public static string LocalDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string SettingsFile => Path.Combine(RoamingDataDir, "settings.json");
}
