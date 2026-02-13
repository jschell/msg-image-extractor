# Implementation Plan: MSG Image Extractor

## Context

The codebase has a complete architectural spec (`docs/working-spec.md`) and project scaffold (`.csproj`, `.sln`, `App.xaml`, all source files as TODO stubs). Nothing is implemented yet. This plan covers implementing every component from stubs to a working WPF tray application that extracts images from `.msg` files.

Skills applied: **autonomous-work** (plan-then-execute with checkpoints), **test-driven-development** (RED-GREEN-REFACTOR, no production code without failing test first), **writing-plans** (exact file paths, code samples, commands).

Plans are saved to `docs/plans/1_backlog/` per the autonomous-work skill; execution is driven by the executing-plans skill after human approval.

---

## Architecture Summary

**Dependency order (bottom-up):**
1. `Logger` — no deps
2. `Settings` — no deps (writes via `System.Text.Json`)
3. `ExtractionEngine` — depends on Settings, Logger, MsgReader
4. `ExtractionQueue` — depends on ExtractionEngine, Logger
5. `FileWatcher` — depends on Logger, forwards to ExtractionQueue
6. `NotificationManager` — WinForms NotifyIcon wrapper
7. `SystemTrayIcon` — depends on NotificationManager, ExtractionQueue, FileWatcher, Settings
8. `SettingsWindow` — depends on Settings, ExtractionQueue
9. `App.xaml.cs` — orchestrator, all components

**Test project:** Needs to be added to solution alongside `tests/` directory.

**Key NuGet dependency:** `MsgReader` v6.* (already in `.csproj`)

---

## Critical Files

| File | Status |
|---|---|
| `src/Core/Logger.cs` | Stub — implement |
| `src/Core/Settings.cs` | Stub — implement |
| `src/Core/ExtractionEngine.cs` | Stub — implement |
| `src/Core/ExtractionQueue.cs` | Stub — implement |
| `src/Core/FileWatcher.cs` | Stub — implement |
| `src/UI/NotificationManager.cs` | Stub — implement |
| `src/UI/SystemTrayIcon.cs` | Stub — implement |
| `src/UI/SettingsWindow.xaml` | Stub — implement |
| `src/UI/SettingsWindow.xaml.cs` | Stub — implement |
| `src/App.xaml.cs` | Stub — implement |
| `MsgImageExtractor.csproj` | Exists — add test project reference |
| `tests/` | Empty — add test project |

---

## Task Breakdown

### Task 1: Test Project Setup

**Files:** `tests/MsgImageExtractor.Tests.csproj`, `MsgImageExtractor.sln`

#### Steps:
1. Create `tests/MsgImageExtractor.Tests.csproj`:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net8.0-windows</TargetFramework>
       <UseWPF>true</UseWPF>
       <Nullable>enable</Nullable>
       <ImplicitUsings>enable</ImplicitUsings>
       <Platforms>x64</Platforms>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
       <PackageReference Include="xunit" Version="2.*" />
       <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
       <PackageReference Include="MsgReader" Version="6.*" />
     </ItemGroup>
     <ItemGroup>
       <ProjectReference Include="..\MsgImageExtractor.csproj" />
     </ItemGroup>
   </Project>
   ```
2. Add test project to `MsgImageExtractor.sln`
3. Run `dotnet build MsgImageExtractor.sln -c Release` — expect success
4. Run `dotnet test MsgImageExtractor.sln -c Release` — expect 0 tests, no failures
5. Commit: "chore: add test project"

---

### Task 2: Logger

**File:** `src/Core/Logger.cs`
**Test:** `tests/Core/LoggerTests.cs`

Spec: Plain file logger. Path: `%APPDATA%\MsgImageExtractor\logs\msgextractor-{yyyy-MM-dd}.log`. Format: `{HH:mm:ss.fff} [{LEVEL}] {message}`. Levels: Info, Warning, Error. `StreamWriter` with `AutoFlush = true`, `lock` for thread safety. New `StreamWriter` when date rolls over. `Flush()` call on shutdown.

#### Steps:
1. Write failing test — `LoggerTests.cs`:
   - `WritesInfo_CreatesLogFile` — write an Info entry, verify file exists
   - `WritesWarning_ContainsWarnLevel` — verify `[WARN]` in output
   - `WritesError_ContainsErrorLevel` — verify `[ERROR]` in output
   - `Flush_DoesNotThrow` — call Flush(), no exception
2. Run tests — expect compile failure (Logger not implemented)
3. Implement `Logger.cs`:
   ```csharp
   public enum LogLevel { Info, Warning, Error }

   public sealed class Logger : IDisposable
   {
       private readonly string _logDirectory;
       private StreamWriter? _writer;
       private DateOnly _currentDate;
       private readonly object _lock = new();

       public Logger(string logDirectory) { _logDirectory = logDirectory; }

       public void Info(string message) => Write(LogLevel.Info, message);
       public void Warning(string message) => Write(LogLevel.Warning, message);
       public void Error(string message) => Write(LogLevel.Error, message);

       private void Write(LogLevel level, string message)
       {
           lock (_lock)
           {
               EnsureWriter();
               var prefix = level switch {
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
               var path = Path.Combine(_logDirectory,
                   $"msgextractor-{today:yyyy-MM-dd}.log");
               _writer = new StreamWriter(path, append: true) { AutoFlush = true };
           }
       }

       public void Flush() { lock (_lock) { _writer?.Flush(); } }
       public void Dispose() { lock (_lock) { _writer?.Dispose(); } }
   }
   ```
4. Run tests — expect all pass
5. Commit: "feat: implement Logger"

---

### Task 3: Settings

**File:** `src/Core/Settings.cs`
**Test:** `tests/Core/SettingsTests.cs`

Spec: Persist to `%APPDATA%\MsgImageExtractor\settings.json`. Write on every change. Clamp `MinAttachmentSizeKb` (1–10,000). Defaults as spec'd. `OutputStrategy` enum: `SameFolder`, `CustomFolder`.

#### Steps:
1. Write failing tests:
   - `Load_MissingFile_ReturnsDefaults`
   - `Load_CorruptJson_ReturnsDefaults`
   - `Save_ThenLoad_RoundTrips`
   - `Clamp_MinAttachmentSizeKb_BelowMin` — value 0 clamped to 1
   - `Clamp_MinAttachmentSizeKb_AboveMax` — value 99999 clamped to 10000
2. Run tests — expect failure
3. Implement `Settings.cs`:
   ```csharp
   public enum OutputStrategy { SameFolder, CustomFolder }

   public sealed class Settings
   {
       public string WatchedFolder { get; set; } = string.Empty;
       public OutputStrategy OutputStrategy { get; set; } = OutputStrategy.SameFolder;
       public string CustomFolder { get; set; } = string.Empty;
       public int MaxConcurrentExtractions { get; set; } = 2;
       public bool SkipExisting { get; set; } = true;
       public bool IncludeSubdirectories { get; set; } = true;
       public string FileNamingPattern { get; set; } = "{msgname}_{attachment}";
       public int MinAttachmentSizeKb { get; set; } = 10;
       public bool ExtractInlineImages { get; set; } = false;
       public bool RecurseEmbeddedMessages { get; set; } = false;
       public bool DeduplicateByHash { get; set; } = true;

       private static readonly string SettingsPath = Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
           "MsgImageExtractor", "settings.json");

       public static Settings Load()
       {
           try
           {
               if (!File.Exists(SettingsPath)) return new Settings();
               var json = File.ReadAllText(SettingsPath);
               var s = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
               s.Clamp();
               return s;
           }
           catch { return new Settings(); }
       }

       public void Save()
       {
           try
           {
               Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
               File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this,
                   new JsonSerializerOptions { WriteIndented = true }));
           }
           catch { /* logged by caller */ }
       }

       private void Clamp()
       {
           MinAttachmentSizeKb = Math.Clamp(MinAttachmentSizeKb, 1, 10_000);
           MaxConcurrentExtractions = Math.Clamp(MaxConcurrentExtractions, 1, 8);
       }
   }
   ```
4. Run tests — expect all pass
5. Commit: "feat: implement Settings"

---

### Task 4: ExtractionEngine

**File:** `src/Core/ExtractionEngine.cs`
**Test:** `tests/Core/ExtractionEngineTests.cs`

Spec: receives `.msg` path → SkipExisting check → TryOpenFile → MsgReader → filter attachments → write output. Returns `ExtractionResult`. Naming collision suffix. `RecurseEmbeddedMessages` depth cap 3. Illegal chars replaced with `_`.

Key types to define in this file:
```csharp
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
    PasswordProtected, FileInUse, UnsupportedFormat, IoError, UnexpectedError
}
```

#### Steps:
1. Write failing tests:
   - `BuildOutputFilename_ReplacesIllegalChars`
   - `BuildOutputFilename_CollisionAddsCounterSuffix` — verify `_2`, `_3` suffixes
   - `BuildOutputFilename_DefaultPattern_MsgnameAndAttachment`
   - `ExtractAsync_FileInUse_ReturnsFileInUseResult` (mock TryOpenFile)
   - `ExtractAsync_AlreadyProcessed_ReturnsAlreadyProcessed` (create sentinel first)
2. Run tests — expect failure
3. Implement `ExtractionEngine.cs` with:
   - `TryOpenFile(string path) → bool`
   - `GetSentinelPath(string sourcePath) → string` (SHA-256 of path → hex filename in `processed\`)
   - `GetOutputPath(string msgPath, string attachmentName, Settings settings) → string`
   - `SanitiseName(string name) → string` (replace `\ / : * ? " < > |` with `_`)
   - `ResolveCollision(string proposedPath) → string` (counter suffix before extension)
   - `ExtractAsync(string msgPath, Settings settings, ConcurrentDictionary<string,byte> hashSet, CancellationToken ct) → Task<ExtractionResult>`
4. Run tests — expect all pass
5. Commit: "feat: implement ExtractionEngine"

---

### Task 5: ExtractionQueue

**File:** `src/Core/ExtractionQueue.cs`
**Test:** `tests/Core/ExtractionQueueTests.cs`

Spec: Bounded `Channel<string>` (1024, `DropWrite`). `MaxConcurrentExtractions` workers. `BlockWorkers`/`UnblockWorkers` via `SemaphoreSlim`. Events: `ExtractionCompleted`, `QueueCountChanged`. Session counters. `RetryChannel` (unbounded) for 30s `FileInUse` retry. `_shutdownCts` for cleanup. Image dedup `ConcurrentDictionary<string,byte>` (suspend at 500k).

#### Steps:
1. Write failing tests:
   - `Enqueue_BeyondCapacity_DropsAndIncrementsDroppedCount`
   - `BlockWorkers_DrainInFlight_ThenUnblock`
   - `ExtractionCompleted_FiredAfterProcessing`
   - `QueueCountChanged_FiredOnEnqueueAndDequeue`
   - `SessionCounters_ExtractedCount_SumsImageCount`
2. Run tests — expect failure
3. Implement `ExtractionQueue.cs`
4. Run tests — expect pass
5. Commit: "feat: implement ExtractionQueue"

---

### Task 6: FileWatcher

**File:** `src/Core/FileWatcher.cs`
**Test:** `tests/Core/FileWatcherTests.cs`

Spec: Watches `*.msg` + `*.MSG`. 64 KB buffer. `Created` + `Renamed` events. 2-second dedup window (`ConcurrentDictionary<string, DateTime>`). Auto-restart on watcher error (3 retries, 10s delay).

#### Steps:
1. Write failing tests:
   - `Start_InvalidPath_LogsError`
   - `DedupWindow_SamePathWithin2s_FiresOnce`
2. Run tests — expect failure
3. Implement `FileWatcher.cs`:
   ```csharp
   public sealed class FileWatcher : IDisposable
   {
       public event Action<string>? FileDetected;
       // ...
       private readonly ConcurrentDictionary<string, DateTime> _seen = new();
       private FileSystemWatcher? _fsw;

       public void Start(string path, bool includeSubdirs) { /* create FSW */ }
       public void Stop() { _fsw?.Dispose(); }
       public void Dispose() => Stop();

       private void OnFileEvent(string fullPath)
       {
           var now = DateTime.UtcNow;
           if (_seen.TryGetValue(fullPath, out var last) && (now - last).TotalSeconds < 2)
               return;
           _seen[fullPath] = now;
           FileDetected?.Invoke(fullPath);
       }
   }
   ```
4. Run tests — expect pass
5. Commit: "feat: implement FileWatcher"

---

### Task 7: NotificationManager

**File:** `src/UI/NotificationManager.cs`
**Test:** `tests/UI/NotificationManagerTests.cs` (minimal — hard to unit test WinForms balloon)

Spec: Routes all tray balloons. `NotifyIcon.ShowBalloonTip`. Title always `"MSG Image Extractor"`. Timeout: 3000ms Info, 5000ms Warning.

#### Steps:
1. Write failing test — `NotificationManager_CanConstruct_WithNotifyIcon`
2. Implement `NotificationManager.cs`:
   ```csharp
   public sealed class NotificationManager(NotifyIcon icon)
   {
       private const string AppTitle = "MSG Image Extractor";

       public void ShowInfo(string message) =>
           icon.ShowBalloonTip(3000, AppTitle, message, ToolTipIcon.Info);

       public void ShowWarning(string message) =>
           icon.ShowBalloonTip(5000, AppTitle, message, ToolTipIcon.Warning);
   }
   ```
3. Run tests — expect pass
4. Commit: "feat: implement NotificationManager"

---

### Task 8: SystemTrayIcon

**File:** `src/UI/SystemTrayIcon.cs`
**Test:** `tests/UI/SystemTrayIconTests.cs`

Spec: WinForms `NotifyIcon`. Status string with zero-suppression (extracted → failed → skipped → dropped → pending retry). Pause/Resume toggle. `PausedSince` timestamp. Menu items: Status, Pause/Resume, Extract From Folder..., Settings, Exit.

#### Steps:
1. Write failing tests for status string construction:
   - `BuildStatus_AllZero_ReturnsMonitoringOnly`
   - `BuildStatus_ExtractedOnly_ShowsExtracted`
   - `BuildStatus_AllCounters_ShowsAll`
   - `BuildStatus_Paused_ShowsPausedSince`
2. Run tests — expect failure
3. Implement `SystemTrayIcon.cs` (status string builder as internal static method for testability)
4. Run tests — expect pass
5. Commit: "feat: implement SystemTrayIcon"

---

### Task 9: SettingsWindow

**Files:** `src/UI/SettingsWindow.xaml`, `src/UI/SettingsWindow.xaml.cs`

Spec: All controls per spec. No Save/Cancel — immediate persist on change. Two distinct buttons: "Reset processed file history" and "Clear image dedup cache". Conditional visibility: output folder only when `CustomFolder`. First-run mode highlights watched folder + output strategy.

#### Steps:
1. Write failing test — `SettingsWindow_CanInstantiate` (smoke test)
2. Implement XAML layout with all controls in order:
   - Watched folder picker
   - Output strategy dropdown (SameFolder / CustomFolder)
   - Output folder picker (Visibility bound to OutputStrategy)
   - Max concurrent slider (1–8)
   - Min attachment size numeric (1–10,000)
   - Deduplicate checkbox
   - Extract inline images checkbox
   - Recurse embedded messages checkbox
   - Reset processed file history button
   - Clear image dedup cache button
3. Implement code-behind: bind controls to `Settings` object, save on each change
4. Run tests — expect pass
5. Commit: "feat: implement SettingsWindow"

---

### Task 10: App.xaml.cs (Orchestrator)

**File:** `src/App.xaml.cs`

Spec: Full startup and shutdown sequences per spec.

Startup order:
1. Named mutex `"Global\MsgImageExtractorSingleInstance"` — exit if held
2. Directory init (`%APPDATA%\MsgImageExtractor\`, `logs\`, `processed\`)
3. Settings load + clamp
4. Sentinel pruning (foreground — blocks watcher start)
5. First-run check → open SettingsWindow if `WatchedFolder` empty
6. Init `ExtractionQueue` → start `FileWatcher`
7. Show tray icon

Shutdown order:
1. `_watcher.EnableRaisingEvents = false`
2. `_shutdownCts.Cancel()`
3. Drain in-flight workers (wait for completion)
4. `Logger.Flush()`

#### Steps:
1. Write failing test — `App_SingleInstance_SecondCallExits` (use mutex directly)
2. Implement `App.xaml.cs`
3. Wire `ExtractionQueue.ExtractionCompleted` → `NotificationManager` (FileInUse retry exhausted, PasswordProtected, first drop)
4. Wire queue/watcher events → `SystemTrayIcon` status refresh
5. Implement Pause/Resume (orchestrator: `_watcher.EnableRaisingEvents` + `_queue.BlockWorkers/UnblockWorkers`)
6. Implement "Extract From Folder..." menu action
7. Run `dotnet build MsgImageExtractor.sln -c Release` — expect success
8. Run `dotnet test MsgImageExtractor.sln -c Release` — expect all pass
9. Commit: "feat: implement App orchestrator — startup/shutdown"

---

### Task 11: Final Integration Verification

#### Steps:
1. Run `dotnet build MsgImageExtractor.sln -c Release` — clean build
2. Run `dotnet test MsgImageExtractor.sln -c Release` — all tests pass
3. Run `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` — produces single `.exe`
4. Commit: "chore: verified build and publish"

---

## Execution Model

Use **subagent-driven-development** skill: one subagent per Task, with review checkpoint after each. Save plans to `docs/plans/1_backlog/` first; human approves before execution begins.

Each subagent should:
1. Read this plan section
2. Follow TDD (RED-GREEN-REFACTOR) exactly
3. Run build + tests after each step
4. Commit at end of task

## Verification

End-to-end verification:
```bash
dotnet build MsgImageExtractor.sln -c Release
dotnet test MsgImageExtractor.sln -c Release
dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Expected: zero build errors, all tests green, single `MsgImageExtractor.exe` artifact.
