using MsgImageExtractor.Core;
using System.Windows.Forms;

namespace MsgImageExtractor.UI;

/// <summary>
/// WinForms NotifyIcon tray icon with context menu.
/// Status string follows zero-suppression rule: counters with value 0 are omitted.
/// Counter order: extracted → failed → skipped → dropped → pending retry.
/// </summary>
public sealed class SystemTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly NotificationManager _notifications;

    // Context menu items
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly ToolStripMenuItem _processExistingItem;
    private readonly ToolStripMenuItem _extractFromFolderItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;

    // State
    private bool _paused;
    private DateTime _since;
    private bool _configured;

    // Events raised to App orchestrator
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? ProcessExistingRequested;
    public event Action? ExtractFromFolderRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public SystemTrayIcon(NotifyIcon notifyIcon, NotificationManager notifications)
    {
        _notifyIcon = notifyIcon;
        _notifications = notifications;
        _since = DateTime.Now;

        // Build context menu
        _statusItem = new ToolStripMenuItem("Monitoring...") { Enabled = false };
        _pauseResumeItem = new ToolStripMenuItem("Pause", null, OnPauseResume);
        _processExistingItem = new ToolStripMenuItem("Process Existing Files", null, OnProcessExisting)
        {
            ToolTipText = "Queue every .msg file already in the watched folder. Previously processed files are skipped."
        };
        _extractFromFolderItem = new ToolStripMenuItem("Extract From Folder...", null, OnExtractFromFolder);
        _settingsItem = new ToolStripMenuItem("Settings", null, OnSettings);
        _exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _pauseResumeItem,
            _processExistingItem,
            _extractFromFolderItem,
            new ToolStripSeparator(),
            _settingsItem,
            new ToolStripSeparator(),
            _exitItem
        });

        _notifyIcon.ContextMenuStrip = menu;
        // A NotifyIcon with no Icon is never shown by Windows — always assign one.
        _notifyIcon.Icon = LoadTrayIcon();
        _notifyIcon.DoubleClick += OnSettings;
        _notifyIcon.Text = "MSG Image Extractor";
        _notifyIcon.Visible = true;
    }

    // -------------------------------------------------------------------------
    // Configuration state
    // -------------------------------------------------------------------------

    public void SetConfigured(bool configured, DateTime? since = null)
    {
        _configured = configured;
        if (since.HasValue)
            _since = since.Value;

        _pauseResumeItem.Visible = configured;
        _processExistingItem.Visible = configured;
        RefreshStatus(0, 0, 0, 0, 0);
    }

    // -------------------------------------------------------------------------
    // Status refresh (called by App on queue events)
    // -------------------------------------------------------------------------

    public void RefreshStatus(long extracted, long failed, long skipped, long dropped, int pendingRetry)
    {
        var text = _configured
            ? BuildStatusString(_paused, _since, extracted, failed, skipped, dropped, pendingRetry)
            : BuildNotConfiguredString();

        _statusItem.Text = text;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon.Text max 63 chars
    }

    // -------------------------------------------------------------------------
    // Static helpers (testable without WinForms handles)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build the status string per spec: zero-suppression, fixed counter order.
    /// </summary>
    public static string BuildStatusString(
        bool paused,
        DateTime since,
        long extracted,
        long failed,
        long skipped,
        long dropped,
        int pendingRetry)
    {
        var verb = paused ? "Paused" : "Monitoring";
        var sb = new System.Text.StringBuilder($"{verb} since {since:HH:mm}");

        // Fixed counter order per spec: extracted → failed → skipped → dropped → pending retry
        var parts = new List<string>(5);
        if (extracted > 0) parts.Add($"{extracted} extracted");
        if (failed > 0)    parts.Add($"{failed} failed");
        if (skipped > 0)   parts.Add($"{skipped} skipped");
        if (dropped > 0)   parts.Add($"{dropped} dropped");
        if (pendingRetry > 0) parts.Add($"{pendingRetry} pending retry");

        if (parts.Count > 0)
        {
            sb.Append(" \u2014 "); // em dash
            sb.Append(string.Join(", ", parts));
        }

        return sb.ToString();
    }

    public static string BuildNotConfiguredString() =>
        "Not configured — click Settings to set up";

    // -------------------------------------------------------------------------
    // Pause / Resume
    // -------------------------------------------------------------------------

    private void OnPauseResume(object? sender, EventArgs e)
    {
        if (_paused)
        {
            _paused = false;
            _pauseResumeItem.Text = "Pause";
            ResumeRequested?.Invoke();
        }
        else
        {
            _paused = true;
            _since = DateTime.Now;
            _pauseResumeItem.Text = "Resume";
            PauseRequested?.Invoke();
        }
    }

    // -------------------------------------------------------------------------
    // Menu handlers
    // -------------------------------------------------------------------------

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch { /* fall back to stock icon */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnProcessExisting(object? sender, EventArgs e) =>
        ProcessExistingRequested?.Invoke();

    private void OnExtractFromFolder(object? sender, EventArgs e) =>
        ExtractFromFolderRequested?.Invoke();

    private void OnSettings(object? sender, EventArgs e) =>
        SettingsRequested?.Invoke();

    private void OnExit(object? sender, EventArgs e) =>
        ExitRequested?.Invoke();

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
