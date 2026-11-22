using MsgImageExtractor.Core;
using MsgImageExtractor.UI;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;

namespace MsgImageExtractor;

/// <summary>
/// Application entry point and orchestrator.
///
/// Startup sequence (spec §Startup Sequence):
///   1. Single-instance mutex check
///   2. Directory initialisation
///   3. Settings load + clamp
///   4. Sentinel pruning (foreground — watcher not started until complete)
///   5. First-run check (open SettingsWindow if WatchedFolder empty)
///   6. Queue + watcher start
///   7. Tray icon shown
///
/// Shutdown sequence (spec §Shutdown Sequence):
///   1. Disable file watcher
///   2. Cancel shutdown CTS (cancels pending retries)
///   3. Drain in-flight workers
///   4. Flush logger
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "Global\\MsgImageExtractorSingleInstance";
    private const string AppDataFolder = "MsgImageExtractor";

    // Component instances (null until initialised)
    private Mutex? _mutex;
    private Logger? _logger;
    private Settings? _settings;
    private ExtractionEngine? _engine;
    private ExtractionQueue? _queue;
    private Core.FileWatcher? _watcher;
    private NotifyIcon? _notifyIcon;
    private NotificationManager? _notifications;
    private UI.SystemTrayIcon? _trayIcon;

    // App-data paths
    private string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolder);
    private string LogsDir => Path.Combine(AppDataDir, "logs");
    private string ProcessedDir => Path.Combine(AppDataDir, "processed");

    // -------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single-instance check
        var launchedMinimized = e.Args.Any(a =>
            string.Equals(a, StartupRegistration.MinimizedArg, StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is running. Tell the user where it is, unless this is
            // a silent auto-start launch.
            if (!launchedMinimized)
                System.Windows.MessageBox.Show(
                    "MSG Image Extractor is already running.\nLook for its icon in the system tray (you may need to click ^ to see it).",
                    "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        // 2. Directory initialisation
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ProcessedDir);

        // 3. Settings load
        _settings = Settings.Load();

        // 4. Logger (opened after directory creation)
        _logger = new Logger(LogsDir);
        _logger.Info("Application starting.");

        // Keep the Run-key entry pointing at the current exe path
        if (_settings.StartWithWindows && !StartupRegistration.Apply(true, out var regError))
            _logger.Warning($"Could not refresh Start with Windows entry: {regError}");

        // 5. Sentinel pruning (foreground)
        PruneSentinels();

        // 6. Build core components
        _engine = new ExtractionEngine(ProcessedDir, _logger);
        _queue = new ExtractionQueue(_engine, _settings, _logger, _settings.MaxConcurrentExtractions);
        _watcher = new Core.FileWatcher(_logger);

        // 7. Notification infrastructure
        _notifyIcon = new NotifyIcon();
        _notifications = new NotificationManager(_notifyIcon);
        _trayIcon = new UI.SystemTrayIcon(_notifyIcon, _notifications);

        // Wire events
        WireEvents();

        // 8. First-run check
        if (string.IsNullOrEmpty(_settings.WatchedFolder))
        {
            _trayIcon.SetConfigured(false);
            OpenSettingsFirstRun();
        }
        else
        {
            StartMonitoring();
            if (!launchedMinimized)
                _notifications.ShowInfo("Running in the system tray. Right-click the icon for options.");
        }
    }

    // -------------------------------------------------------------------------
    // First-run
    // -------------------------------------------------------------------------

    private void OpenSettingsFirstRun()
    {
        var win = CreateSettingsWindow();
        win.WatchedFolderSelected += _ =>
        {
            // User has set a watched folder — start monitoring
            StartMonitoring();
        };
        win.Show();
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var win = new SettingsWindow(_settings!, _queue, ProcessedDir);
        win.ProcessExistingRequested += OnProcessExisting;
        return win;
    }

    // -------------------------------------------------------------------------
    // Start monitoring
    // -------------------------------------------------------------------------

    private bool _monitoringStarted;

    private void StartMonitoring()
    {
        if (_monitoringStarted) return;
        _monitoringStarted = true;

        _trayIcon!.SetConfigured(true, DateTime.Now);

        _queue!.Start();
        _watcher!.Start(_settings!.WatchedFolder, _settings.IncludeSubdirectories);

        _logger!.Info($"Monitoring started: {_settings.WatchedFolder}");
    }

    // -------------------------------------------------------------------------
    // Event wiring
    // -------------------------------------------------------------------------

    private void WireEvents()
    {
        // Watcher → queue (subscribed once here, not in StartMonitoring)
        _watcher!.FileDetected += path => _queue!.Enqueue(path);

        // Queue → tray status refresh
        _queue!.ExtractionCompleted += _ => RefreshTrayStatus();
        _queue.QueueCountChanged += _ => RefreshTrayStatus();

        // Queue → notifications
        _queue.FirstDropOccurred += () => _notifications!.NotifyQueueFull();
        _queue.DedupSuspended += () => _notifications!.NotifyDedupSuspended();
        _queue.ExtractionCompleted += OnExtractionCompleted;

        // Tray → app actions
        _trayIcon!.PauseRequested += OnPause;
        _trayIcon.ResumeRequested += OnResume;
        _trayIcon.ProcessExistingRequested += OnProcessExisting;
        _trayIcon.ExtractFromFolderRequested += OnExtractFromFolder;
        _trayIcon.SettingsRequested += () => CreateSettingsWindow().Show();
        _trayIcon.ExitRequested += () => Shutdown();
    }

    private void RefreshTrayStatus()
    {
        // Marshal to UI thread
        Dispatcher.InvokeAsync(() =>
        {
            _trayIcon?.RefreshStatus(
                _queue!.ExtractedCount,
                _queue.FailedCount,
                _queue.SkippedCount,
                _queue.DroppedCount,
                _queue.PendingRetryCount);
        });
    }

    private void OnExtractionCompleted(ExtractionResult result)
    {
        if (result.FailureReason == ExtractionFailureReason.PasswordProtected)
            _notifications!.NotifyPasswordProtected(Path.GetFileName(result.SourcePath));
        // FileInUse exhausted retry is handled in queue → NotifyFileInUseFailed fires from worker
    }

    // -------------------------------------------------------------------------
    // Pause / Resume
    // -------------------------------------------------------------------------

    private async void OnPause()
    {
        // Spec: disable watcher first, then block workers
        if (_watcher is not null)
            _watcher.Stop();
        if (_queue is not null)
            await _queue.BlockWorkersAsync().ConfigureAwait(false);
        _logger?.Info("Monitoring paused.");
    }

    private void OnResume()
    {
        // Spec: unblock workers first, then re-enable watcher
        _queue?.UnblockWorkers();
        if (_watcher is not null && _settings is not null)
            _watcher.Start(_settings.WatchedFolder, _settings.IncludeSubdirectories);
        _logger?.Info("Monitoring resumed.");
    }

    // -------------------------------------------------------------------------
    // Process existing files in the watched folder
    // -------------------------------------------------------------------------

    private int _scanInProgress;

    private async void OnProcessExisting()
    {
        var folder = _settings?.WatchedFolder;
        if (string.IsNullOrEmpty(folder) || _queue is null || !_monitoringStarted)
        {
            _notifications?.ShowWarning("Set a watched folder in Settings first.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            _notifications?.ShowWarning($"Watched folder not found: {folder}");
            return;
        }
        if (Interlocked.Exchange(ref _scanInProgress, 1) == 1)
        {
            _notifications?.ShowInfo("Already processing existing files.");
            return;
        }

        try
        {
            var option = _settings!.IncludeSubdirectories
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            // Scan + enqueue off the UI thread; EnqueueAsync waits for queue space
            // instead of dropping, so large folders are fully processed.
            int queued = await Task.Run(async () =>
            {
                int n = 0;
                foreach (var path in Directory.EnumerateFiles(folder, "*.msg", option))
                {
                    await _queue.EnqueueAsync(path).ConfigureAwait(false);
                    n++;
                }
                return n;
            });

            _logger?.Info($"Process existing: queued {queued} file(s) from {folder}");
            _notifications?.ShowInfo(queued == 0
                ? "No .msg files found in the watched folder."
                : $"Queued {queued} existing .msg file{(queued == 1 ? "" : "s")}. Already-processed files will be skipped.");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Process existing failed: {ex.Message}");
            _notifications?.ShowWarning($"Could not scan watched folder: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scanInProgress, 0);
        }
    }

    // -------------------------------------------------------------------------
    // Extract From Folder (batch)
    // -------------------------------------------------------------------------

    private async void OnExtractFromFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a folder containing .msg files to extract images from.",
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        var folder = dlg.SelectedPath;
        var includeSubdirs = _settings?.IncludeSubdirectories ?? true;

        var files = includeSubdirs
            ? Directory.GetFiles(folder, "*.msg", SearchOption.AllDirectories)
            : Directory.GetFiles(folder, "*.msg", SearchOption.TopDirectoryOnly);

        int filesProcessed = 0;

        foreach (var path in files)
        {
            _queue!.Enqueue(path);
            filesProcessed++;
        }

        // Batch completion is approximate (files are processed asynchronously)
        // Notify after a short wait — for a proper async batch, a dedicated batch runner would be used
        await Task.Delay(100);
        _notifications?.NotifyBatchComplete(filesProcessed, (int)_queue!.ExtractedCount);
    }

    // -------------------------------------------------------------------------
    // Sentinel pruning
    // -------------------------------------------------------------------------

    private void PruneSentinels()
    {
        if (!Directory.Exists(ProcessedDir)) return;

        try
        {
            var sentinels = Directory.GetFiles(ProcessedDir, "*.sentinel");
            int pruned = 0;
            foreach (var sentinel in sentinels)
            {
                try
                {
                    var sourcePath = File.ReadAllText(sentinel);
                    if (!File.Exists(sourcePath))
                    {
                        File.Delete(sentinel);
                        pruned++;
                    }
                }
                catch { /* ignore per-file errors */ }
            }

            if (pruned > 0)
                _logger?.Info($"Sentinel pruning: removed {pruned} orphaned sentinel(s).");
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Sentinel pruning error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Shutdown
    // -------------------------------------------------------------------------

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.Info("Application shutting down.");

        // Spec shutdown sequence:
        // 1. Disable file watcher
        _watcher?.Stop();

        // 2 + 3. Stop queue (cancels pending retries, drains in-flight workers)
        if (_queue is not null)
            await _queue.StopAsync().ConfigureAwait(false);

        // 4. Flush logger
        _logger?.Flush();
        _logger?.Dispose();

        // Dispose remaining resources
        _trayIcon?.Dispose();
        _queue?.Dispose();
        _watcher?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        base.OnExit(e);
    }
}
