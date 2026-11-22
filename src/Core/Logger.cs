using System.IO;

namespace MsgImageExtractor.Core;

public enum LogLevel { Info, Warning, Error }

/// <summary>
/// Plain file logger. One log file per calendar day.
/// File: %APPDATA%\MsgImageExtractor\logs\msgextractor-{yyyy-MM-dd}.log
/// Format: {HH:mm:ss.fff} [{LEVEL}] {message}
/// Thread-safe via lock.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    public Logger(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_logDirectory);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var path = Path.Combine(_logDirectory, $"msgextractor-{today:yyyy-MM-dd}.log");
            var prefix = level switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                _ => "INFO"
            };
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs);
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{prefix}] {message}");
        }
    }

    /// <summary>
    /// Delete msgextractor-*.log files whose date is older than <paramref name="retentionDays"/>.
    /// Returns the number of files deleted. Never throws.
    /// </summary>
    public int CleanOldLogs(int retentionDays = 30)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(_logDirectory)) return 0;
            var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-retentionDays);
            foreach (var file in Directory.GetFiles(_logDirectory, "msgextractor-*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(file)["msgextractor-".Length..];
                if (DateOnly.TryParseExact(stem, "yyyy-MM-dd", out var date) && date < cutoff)
                {
                    try { File.Delete(file); deleted++; } catch { /* in use — try next start */ }
                }
            }
        }
        catch { /* best effort */ }
        return deleted;
    }

    public void Flush() { } // no-op — no persistent handle

    public void Dispose() { } // no-op — no persistent resources
}
