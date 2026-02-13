using MsgReader.Outlook;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MsgImageExtractor.Core;

// ---------------------------------------------------------------------------
// Result types
// ---------------------------------------------------------------------------

public record ExtractionResult(
    ExtractionFailureReason? FailureReason,
    string SourcePath,
    IReadOnlyList<string> OutputPaths,
    int ExtractedCount,
    int DuplicateImageCount,
    bool AlreadyProcessed,
    string? ErrorMessage,
    TimeSpan Duration)
{
    public bool IsSuccess => FailureReason is null;
}

public enum ExtractionFailureReason
{
    PasswordProtected,
    FileInUse,
    UnsupportedFormat,
    IoError,
    UnexpectedError
}

// ---------------------------------------------------------------------------
// Engine
// ---------------------------------------------------------------------------

/// <summary>
/// Workhorse: receives a .msg path, extracts image attachments, returns ExtractionResult.
/// </summary>
public sealed class ExtractionEngine
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".heic", ".webp"
    };

    private static readonly char[] IllegalChars =
        Path.GetInvalidFileNameChars()
            .Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
            .Distinct()
            .ToArray();

    private const int MaxRecurseDepth = 3;

    private readonly string _processedDir;
    private readonly Logger _logger;

    public ExtractionEngine(string processedDir, Logger logger)
    {
        _processedDir = processedDir;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Public helpers (internal/static for testability)
    // -------------------------------------------------------------------------

    /// <summary>Replace illegal filesystem characters with '_'.</summary>
    public static string SanitiseName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(IllegalChars.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// <summary>Apply the naming pattern and sanitise tokens.</summary>
    public static string BuildOutputFilename(string msgName, string attachmentName, string pattern)
    {
        var safe = pattern
            .Replace("{msgname}", SanitiseName(msgName))
            .Replace("{attachment}", attachmentName); // attachment keeps its extension; sanitised below
        // Sanitise the full result (path separators in attachment filenames)
        return SanitiseName(safe);
    }

    /// <summary>
    /// Given a proposed output path, append _2, _3 … before the extension until a
    /// non-existing path is found. Counter is inserted before the final '.'.
    /// </summary>
    public static string ResolveCollision(string proposedPath)
    {
        if (!File.Exists(proposedPath))
            return proposedPath;

        var dir = Path.GetDirectoryName(proposedPath) ?? string.Empty;
        var filename = Path.GetFileName(proposedPath);
        var lastDot = filename.LastIndexOf('.');
        string stem, ext;
        if (lastDot > 0)
        {
            stem = filename[..lastDot];
            ext = filename[lastDot..];
        }
        else
        {
            stem = filename;
            ext = string.Empty;
        }

        var counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{stem}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>
    /// Returns the sentinel file path for a given source .msg path.
    /// Keyed on SHA-256 of the full source path → hex string.sentinel
    /// </summary>
    public static string GetSentinelPath(string sourcePath, string processedDir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)));
        return Path.Combine(processedDir, $"{hash}.sentinel");
    }

    // -------------------------------------------------------------------------
    // File lock probe
    // -------------------------------------------------------------------------

    private static bool TryOpenFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Output path resolution
    // -------------------------------------------------------------------------

    private static string GetOutputDirectory(string msgPath, Settings settings)
    {
        if (settings.OutputStrategy == OutputStrategy.CustomFolder &&
            !string.IsNullOrWhiteSpace(settings.CustomFolder))
            return settings.CustomFolder;

        return Path.GetDirectoryName(msgPath) ?? Directory.GetCurrentDirectory();
    }

    // -------------------------------------------------------------------------
    // Main entry point
    // -------------------------------------------------------------------------

    public async Task<ExtractionResult> ExtractAsync(
        string msgPath,
        Settings settings,
        ConcurrentDictionary<string, byte> hashSet,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. SkipExisting check (O(1) — no file handle opened)
        if (settings.SkipExisting)
        {
            var sentinel = GetSentinelPath(msgPath, _processedDir);
            if (File.Exists(sentinel))
            {
                return new ExtractionResult(
                    FailureReason: null,
                    SourcePath: msgPath,
                    OutputPaths: Array.Empty<string>(),
                    ExtractedCount: 0,
                    DuplicateImageCount: 0,
                    AlreadyProcessed: true,
                    ErrorMessage: null,
                    Duration: sw.Elapsed);
            }
        }

        // 2. Lock probe
        if (!TryOpenFile(msgPath))
        {
            return new ExtractionResult(
                FailureReason: ExtractionFailureReason.FileInUse,
                SourcePath: msgPath,
                OutputPaths: Array.Empty<string>(),
                ExtractedCount: 0,
                DuplicateImageCount: 0,
                AlreadyProcessed: false,
                ErrorMessage: "File is in use",
                Duration: sw.Elapsed);
        }

        // 3. Extract
        var outputPaths = new List<string>();
        int duplicateCount = 0;

        try
        {
            using var msg = new Storage.Message(msgPath);
            var msgName = Path.GetFileNameWithoutExtension(msgPath);
            var outDir = GetOutputDirectory(msgPath, settings);
            Directory.CreateDirectory(outDir);

            await ExtractFromMessageAsync(
                msg, msgName, outDir, settings, hashSet,
                outputPaths, ref duplicateCount, depth: 0, ct);

            // Write sentinel on success (even if 0 images extracted)
            if (settings.SkipExisting)
            {
                Directory.CreateDirectory(_processedDir);
                var sentinel = GetSentinelPath(msgPath, _processedDir);
                await File.WriteAllTextAsync(sentinel, msgPath, ct);
            }

            return new ExtractionResult(
                FailureReason: null,
                SourcePath: msgPath,
                OutputPaths: outputPaths,
                ExtractedCount: outputPaths.Count,
                DuplicateImageCount: duplicateCount,
                AlreadyProcessed: false,
                ErrorMessage: null,
                Duration: sw.Elapsed);
        }
        catch (MsgReader.Exceptions.MRPasswordException ex)
        {
            _logger.Warning($"Password-protected: {msgPath} — {ex.Message}");
            return new ExtractionResult(
                FailureReason: ExtractionFailureReason.PasswordProtected,
                SourcePath: msgPath,
                OutputPaths: Array.Empty<string>(),
                ExtractedCount: 0,
                DuplicateImageCount: 0,
                AlreadyProcessed: false,
                ErrorMessage: ex.Message,
                Duration: sw.Elapsed);
        }
        catch (IOException ex)
        {
            _logger.Error($"IoError extracting {msgPath}: {ex.Message}");
            return new ExtractionResult(
                FailureReason: ExtractionFailureReason.IoError,
                SourcePath: msgPath,
                OutputPaths: Array.Empty<string>(),
                ExtractedCount: 0,
                DuplicateImageCount: 0,
                AlreadyProcessed: false,
                ErrorMessage: ex.Message,
                Duration: sw.Elapsed);
        }
        catch (MsgReader.Exceptions.MRFileTypeNotSupported ex)
        {
            _logger.Warning($"Unsupported format: {msgPath} — {ex.Message}");
            return new ExtractionResult(
                FailureReason: ExtractionFailureReason.UnsupportedFormat,
                SourcePath: msgPath,
                OutputPaths: Array.Empty<string>(),
                ExtractedCount: 0,
                DuplicateImageCount: 0,
                AlreadyProcessed: false,
                ErrorMessage: ex.Message,
                Duration: sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"Unexpected error extracting {msgPath}: {ex}");
            return new ExtractionResult(
                FailureReason: ExtractionFailureReason.UnexpectedError,
                SourcePath: msgPath,
                OutputPaths: Array.Empty<string>(),
                ExtractedCount: 0,
                DuplicateImageCount: 0,
                AlreadyProcessed: false,
                ErrorMessage: ex.Message,
                Duration: sw.Elapsed);
        }
    }

    // -------------------------------------------------------------------------
    // Recursive extraction
    // -------------------------------------------------------------------------

    private async Task ExtractFromMessageAsync(
        Storage.Message msg,
        string msgNameChain,
        string outDir,
        Settings settings,
        ConcurrentDictionary<string, byte> hashSet,
        List<string> outputPaths,
        ref int duplicateCount,
        int depth,
        CancellationToken ct)
    {
        foreach (var attachment in msg.Attachments)
        {
            ct.ThrowIfCancellationRequested();

            if (attachment is Storage.Attachment att)
            {
                var ext = Path.GetExtension(att.FileName ?? string.Empty);

                // Recurse into embedded .msg if enabled
                if (settings.RecurseEmbeddedMessages &&
                    string.Equals(ext, ".msg", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth >= MaxRecurseDepth)
                    {
                        _logger.Warning(
                            $"Recursion depth cap ({MaxRecurseDepth}) reached; skipping embedded .msg in {msgNameChain}");
                        continue;
                    }

                    try
                    {
                        var data = att.Data;
                        if (data is null) continue;
                        using var ms = new MemoryStream(data);
                        using var embeddedMsg = new Storage.Message(ms);
                        var embeddedName = Path.GetFileNameWithoutExtension(att.FileName ?? "embedded");
                        var chain = $"{msgNameChain}_{SanitiseName(embeddedName)}";
                        await ExtractFromMessageAsync(
                            embeddedMsg, chain, outDir, settings, hashSet,
                            outputPaths, ref duplicateCount, depth + 1, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to open embedded .msg in {msgNameChain}: {ex.Message}");
                    }

                    continue;
                }

                // Image attachment
                if (!ImageExtensions.Contains(ext)) continue;

                // Inline image filter
                if (!settings.ExtractInlineImages && IsInlineImage(att)) continue;

                // Size filter
                var data2 = att.Data;
                if (data2 is null) continue;
                if (data2.Length < settings.MinAttachmentSizeKb * 1024L) continue;

                // Hash dedup
                if (settings.DeduplicateByHash)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(data2));
                    if (!hashSet.TryAdd(hash, 0))
                    {
                        duplicateCount++;
                        continue;
                    }
                }

                // Write
                var filename = BuildOutputFilename(
                    msgNameChain,
                    att.FileName ?? $"attachment{ext}",
                    settings.FileNamingPattern);
                var proposed = Path.Combine(outDir, filename);
                var finalPath = ResolveCollision(proposed);

                await File.WriteAllBytesAsync(finalPath, data2, ct);
                outputPaths.Add(finalPath);
            }
        }
    }

    private static bool IsInlineImage(Storage.Attachment att)
    {
        // Content-ID (CID) references indicate inline images
        return !string.IsNullOrEmpty(att.ContentId);
    }
}
