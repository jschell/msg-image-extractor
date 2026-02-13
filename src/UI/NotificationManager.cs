using System.Windows.Forms;

namespace MsgImageExtractor.UI;

/// <summary>
/// Routes all tray balloon notifications. SystemTrayIcon never calls ShowBalloonTip directly.
/// Title is always "MSG Image Extractor".
/// Info: 3 000 ms timeout. Warning: 5 000 ms timeout.
/// </summary>
public sealed class NotificationManager
{
    private const string AppTitle = "MSG Image Extractor";
    private const int InfoTimeoutMs = 3_000;
    private const int WarningTimeoutMs = 5_000;

    private readonly NotifyIcon _icon;

    public NotificationManager(NotifyIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _icon = icon;
    }

    /// <summary>Show an informational balloon (3 s).</summary>
    public void ShowInfo(string message) =>
        _icon.ShowBalloonTip(InfoTimeoutMs, AppTitle, message, ToolTipIcon.Info);

    /// <summary>Show a warning balloon (5 s).</summary>
    public void ShowWarning(string message) =>
        _icon.ShowBalloonTip(WarningTimeoutMs, AppTitle, message, ToolTipIcon.Warning);

    // -------------------------------------------------------------------------
    // Spec-defined notification messages
    // -------------------------------------------------------------------------

    public void NotifyQueueFull() =>
        ShowWarning("Queue full — files are being dropped. Use Extract From Folder... to process missed files.");

    public void NotifyDedupSuspended() =>
        ShowWarning("Image deduplication suspended — cache limit reached. Open Settings to clear the cache and resume.");

    public void NotifyFileInUseFailed(string filename) =>
        ShowWarning($"{filename} — could not access file (in use)");

    public void NotifyPasswordProtected(string filename) =>
        ShowWarning($"{filename} — could not extract: message is password-protected");

    public void NotifyBatchComplete(int filesProcessed, int imagesExtracted)
    {
        var msg = imagesExtracted == 0 && filesProcessed > 0
            ? "Batch complete — all files already processed."
            : $"Batch complete — {imagesExtracted} image{(imagesExtracted == 1 ? "" : "s")} extracted from {filesProcessed} file{(filesProcessed == 1 ? "" : "s")}.";
        ShowInfo(msg);
    }
}
