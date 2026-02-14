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
    private StreamWriter? _writer;
    private DateOnly _currentDate;
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
            EnsureWriter();
            var prefix = level switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                _ => "INFO"
            };
            _writer!.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{prefix}] {message}");
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is null || today != _currentDate)
        {
            _writer?.Dispose();
            _currentDate = today;
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, $"msgextractor-{today:yyyy-MM-dd}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _writer?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
