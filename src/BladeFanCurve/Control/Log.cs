using System.Collections.Concurrent;
using System.IO;
using System.Text;
using BladeFanCurve.Config;

namespace BladeFanCurve.Control;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime TimestampLocal, LogLevel Level, string Message)
{
    public override string ToString() => $"{TimestampLocal:HH:mm:ss} [{Level,-5}] {Message}";
}

/// <summary>
/// Small append-only logger with an in-memory ring buffer for the diagnostics pane.
/// Rolls the file over at 1 MB so it cannot grow without bound.
/// </summary>
public static class Log
{
    private const int MaxBufferedEntries = 500;
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly object FileLock = new();

    public static event Action<LogEntry>? EntryWritten;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static IReadOnlyList<LogEntry> Recent() => Buffer.ToArray();

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        Buffer.Enqueue(entry);
        while (Buffer.Count > MaxBufferedEntries) Buffer.TryDequeue(out _);

        try { EntryWritten?.Invoke(entry); } catch { /* a UI handler must not break logging */ }

        if (level == LogLevel.Debug) return;

        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Directory);
                var path = ConfigStore.LogPath;

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                    File.Move(path, path + ".1", overwrite: true);

                File.AppendAllText(path,
                    $"{entry.TimestampLocal:yyyy-MM-dd HH:mm:ss} [{level,-5}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Logging must never throw into the control loop.
            }
        }
    }
}
