using System.Collections.Concurrent;

namespace MsgImageExtractor.Core;

/// <summary>
/// Watches a folder for *.msg files and raises FileDetected for each new arrival.
/// Deduplicates rapid OS events with a 2-second window.
/// Auto-restarts on FSW errors (up to 3 retries).
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private const int InternalBufferBytes = 64 * 1024; // 64 KB
    private const double DedupWindowSeconds = 2.0;
    private const int MaxRestartAttempts = 3;
    private const int RestartDelayMs = 10_000;

    private readonly Logger _logger;
    private FileSystemWatcher? _fsw;
    private string? _watchedPath;
    private bool _includeSubdirs;

    /// <summary>Keyed on full path → last event time (UTC). Stale entries are overwritten.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for each new .msg file path that passes the dedup window.</summary>
    public event Action<string>? FileDetected;

    public FileWatcher(Logger logger)
    {
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Start / Stop
    // -------------------------------------------------------------------------

    public void Start(string path, bool includeSubdirs)
    {
        _watchedPath = path;
        _includeSubdirs = includeSubdirs;
        TryCreateWatcher();
    }

    public void Stop()
    {
        var fsw = _fsw;
        _fsw = null;
        if (fsw is not null)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
    }

    public void Dispose() => Stop();

    // -------------------------------------------------------------------------
    // Watcher creation
    // -------------------------------------------------------------------------

    private void TryCreateWatcher()
    {
        if (string.IsNullOrEmpty(_watchedPath))
            return;

        try
        {
            var fsw = new FileSystemWatcher(_watchedPath)
            {
                Filter = "*.msg",
                IncludeSubdirectories = _includeSubdirs,
                InternalBufferSize = InternalBufferBytes,
                NotifyFilter = NotifyFilters.FileName
            };

            fsw.Created += (_, e) => OnFileEvent(e.FullPath);
            fsw.Renamed += (_, e) => OnFileEvent(e.FullPath);
            fsw.Error += OnWatcherError;

            fsw.EnableRaisingEvents = true;
            _fsw = fsw;
        }
        catch (Exception ex)
        {
            _logger.Error($"FileWatcher failed to start watching '{_watchedPath}': {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Event handling
    // -------------------------------------------------------------------------

    internal void OnFileEvent(string fullPath)
    {
        var now = DateTime.UtcNow;

        if (_seen.TryGetValue(fullPath, out var last) &&
            (now - last).TotalSeconds < DedupWindowSeconds)
        {
            // Duplicate within the dedup window — suppress
            return;
        }

        _seen[fullPath] = now;
        FileDetected?.Invoke(fullPath);
    }

    // -------------------------------------------------------------------------
    // Test helper — allows tests to inject synthetic file events
    // -------------------------------------------------------------------------

    /// <summary>Directly invoke the file event logic (for unit tests).</summary>
    internal void SimulateFileEvent(string fullPath) => OnFileEvent(fullPath);

    // -------------------------------------------------------------------------
    // Error / auto-restart
    // -------------------------------------------------------------------------

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.Warning($"FileWatcher error: {e.GetException()?.Message} — restarting watcher");

        var old = _fsw;
        _fsw = null;
        old?.Dispose();

        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= MaxRestartAttempts; attempt++)
            {
                if (!Directory.Exists(_watchedPath))
                {
                    _logger.Warning(
                        $"Watched folder missing (attempt {attempt}/{MaxRestartAttempts}): {_watchedPath}");
                    if (attempt == MaxRestartAttempts)
                    {
                        _logger.Error(
                            $"Watched folder still missing after {MaxRestartAttempts} attempts — watcher stopped.");
                        return;
                    }
                    await Task.Delay(RestartDelayMs).ConfigureAwait(false);
                    continue;
                }

                TryCreateWatcher();
                if (_fsw is not null)
                {
                    _logger.Info("FileWatcher restarted successfully.");
                    return;
                }

                await Task.Delay(RestartDelayMs).ConfigureAwait(false);
            }
        });
    }
}
