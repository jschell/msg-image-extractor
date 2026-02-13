using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsgImageExtractor.Core;

public enum OutputStrategy { SameFolder, CustomFolder }

/// <summary>
/// Application settings persisted to %APPDATA%\MsgImageExtractor\settings.json.
/// Written on every property change. Clamped on load.
/// </summary>
public sealed class Settings
{
    // --- Watched folder ---
    public string WatchedFolder { get; set; } = string.Empty;

    // --- Output ---
    public OutputStrategy OutputStrategy { get; set; } = OutputStrategy.SameFolder;
    public string CustomFolder { get; set; } = string.Empty;

    // --- Worker pool ---
    public int MaxConcurrentExtractions { get; set; } = 2;  // 1–8

    // --- Skip / dedup ---
    public bool SkipExisting { get; set; } = true;
    public bool DeduplicateByHash { get; set; } = true;

    // --- Advanced (JSON-only, not surfaced in UI) ---
    public bool IncludeSubdirectories { get; set; } = true;
    public string FileNamingPattern { get; set; } = "{msgname}_{attachment}";

    // --- Extraction options ---
    public int MinAttachmentSizeKb { get; set; } = 10;      // 1–10,000
    public bool ExtractInlineImages { get; set; } = false;
    public bool RecurseEmbeddedMessages { get; set; } = false;

    // ---------------------------------------------------------------------------
    // Serialiser options (shared)
    // ---------------------------------------------------------------------------
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ---------------------------------------------------------------------------
    // Well-known path
    // ---------------------------------------------------------------------------
    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MsgImageExtractor",
            "settings.json");

    // ---------------------------------------------------------------------------
    // Load
    // ---------------------------------------------------------------------------

    /// <summary>Load from the default %APPDATA% path.</summary>
    public static Settings Load() => LoadFrom(DefaultSettingsPath);

    /// <summary>Load from an explicit path (used in tests).</summary>
    public static Settings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Settings();

            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json, _jsonOptions) ?? new Settings();
            s.Clamp();
            return s;
        }
        catch
        {
            return new Settings();
        }
    }

    // ---------------------------------------------------------------------------
    // Save
    // ---------------------------------------------------------------------------

    /// <summary>Save to the default %APPDATA% path. Silently swallows write failures.</summary>
    public void Save() => SaveTo(DefaultSettingsPath);

    /// <summary>Save to an explicit path (used in tests).</summary>
    public void SaveTo(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, _jsonOptions));
        }
        catch
        {
            // Logged by caller; transient write failures are silent.
        }
    }

    // ---------------------------------------------------------------------------
    // Clamp
    // ---------------------------------------------------------------------------
    private void Clamp()
    {
        MinAttachmentSizeKb = Math.Clamp(MinAttachmentSizeKb, 1, 10_000);
        MaxConcurrentExtractions = Math.Clamp(MaxConcurrentExtractions, 1, 8);
    }
}
