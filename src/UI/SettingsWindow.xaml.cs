using MsgImageExtractor.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace MsgImageExtractor.UI;

/// <summary>
/// Settings window — all changes are applied and persisted immediately.
/// No Save/Cancel button. Closing is always safe.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly ExtractionQueue? _queue;
    private readonly string _processedDir;
    private bool _initialising = true;

    // Raised when WatchedFolder is set for the first time (first-run flow)
    public event Action<string>? WatchedFolderSelected;

    public SettingsWindow(Settings settings, ExtractionQueue? queue, string processedDir)
    {
        InitializeComponent();
        _settings = settings;
        _queue = queue;
        _processedDir = processedDir;

        LoadFromSettings();
        _initialising = false;
    }

    // -------------------------------------------------------------------------
    // Load settings into UI controls
    // -------------------------------------------------------------------------

    private void LoadFromSettings()
    {
        WatchedFolderBox.Text = _settings.WatchedFolder;

        // Output strategy
        OutputStrategyCombo.SelectedIndex = _settings.OutputStrategy == OutputStrategy.CustomFolder ? 1 : 0;
        OutputFolderRow.Visibility = _settings.OutputStrategy == OutputStrategy.CustomFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
        OutputFolderBox.Text = _settings.CustomFolder;

        // Max concurrent slider
        MaxConcurrentSlider.Value = _settings.MaxConcurrentExtractions;
        MaxConcurrentLabel.Text = _settings.MaxConcurrentExtractions.ToString();

        // Min attachment size
        MinSizeBox.Text = _settings.MinAttachmentSizeKb.ToString();

        // Checkboxes
        DeduplicateCheck.IsChecked = _settings.DeduplicateByHash;
        InlineImagesCheck.IsChecked = _settings.ExtractInlineImages;
        RecurseEmbeddedCheck.IsChecked = _settings.RecurseEmbeddedMessages;

        UpdateDedupLabel();
    }

    // -------------------------------------------------------------------------
    // Dedup cache warning label
    // -------------------------------------------------------------------------

    private void UpdateDedupLabel()
    {
        if (_queue is not null && _queue.IsDedupSuspended)
            DeduplicateCheck.Content = "Deduplicate identical images (SHA-256) ⚠ suspended — cache full";
        else
            DeduplicateCheck.Content = "Deduplicate identical images (SHA-256)";
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void BrowseWatchedFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose a folder to monitor for .msg files.",
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrEmpty(_settings.WatchedFolder))
            dlg.SelectedPath = _settings.WatchedFolder;

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var wasEmpty = string.IsNullOrEmpty(_settings.WatchedFolder);
            _settings.WatchedFolder = dlg.SelectedPath;
            WatchedFolderBox.Text = dlg.SelectedPath;
            _settings.Save();

            if (wasEmpty)
                WatchedFolderSelected?.Invoke(dlg.SelectedPath);
        }
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose an output folder for extracted images.",
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrEmpty(_settings.CustomFolder))
            dlg.SelectedPath = _settings.CustomFolder;

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.CustomFolder = dlg.SelectedPath;
            OutputFolderBox.Text = dlg.SelectedPath;
            _settings.Save();
        }
    }

    private void OutputStrategy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;

        var selected = (OutputStrategyCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        _settings.OutputStrategy = selected == "CustomFolder"
            ? OutputStrategy.CustomFolder
            : OutputStrategy.SameFolder;

        OutputFolderRow.Visibility = _settings.OutputStrategy == OutputStrategy.CustomFolder
            ? Visibility.Visible
            : Visibility.Collapsed;

        _settings.Save();
    }

    private void MaxConcurrent_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initialising) return;
        var value = (int)MaxConcurrentSlider.Value;
        MaxConcurrentLabel.Text = value.ToString();
        _settings.MaxConcurrentExtractions = value;
        _settings.Save();
    }

    private void MinSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialising) return;
        if (int.TryParse(MinSizeBox.Text, out var value))
        {
            value = Math.Clamp(value, 1, 10_000);
            _settings.MinAttachmentSizeKb = value;
            _settings.Save();
        }
    }

    private void Deduplicate_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialising) return;
        _settings.DeduplicateByHash = DeduplicateCheck.IsChecked == true;
        _settings.Save();
    }

    private void InlineImages_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialising) return;
        _settings.ExtractInlineImages = InlineImagesCheck.IsChecked == true;
        _settings.Save();
    }

    private void RecurseEmbedded_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialising) return;
        _settings.RecurseEmbeddedMessages = RecurseEmbeddedCheck.IsChecked == true;
        _settings.Save();
    }

    private void ResetHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "This will delete all processed file history. All .msg files will be re-extracted on the next run.\n\nContinue?",
            "Reset Processed File History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (Directory.Exists(_processedDir))
                foreach (var f in Directory.GetFiles(_processedDir, "*.sentinel"))
                    File.Delete(f);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to clear history: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDedupCache_Click(object sender, RoutedEventArgs e)
    {
        _queue?.ClearHashSet();
        UpdateDedupLabel();
    }
}
