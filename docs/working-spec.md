# MSG Image Extractor

A Windows system tray utility that monitors folders for Outlook `.msg` files and automatically extracts all image attachments into a specified output directory.

Architecturally mirrors [heic-convert](https://github.com/jschell/heic-convert): same project layout, same Core/UI separation, same channel-based worker queue, same JSON-persisted settings, same single-file self-contained `.exe` deployment.

---

## Application Architecture

Mirrors heic-convert's folder layout exactly:

```
MsgImageExtractor/
├── src/
│   ├── Core/
│   │   ├── ExtractionEngine.cs       ← ConversionEngine equivalent
│   │   ├── ExtractionQueue.cs        ← ConversionQueue equivalent
│   │   ├── FileWatcher.cs            ← identical pattern, watches *.msg
│   │   ├── Logger.cs                 ← identical
│   │   └── Settings.cs               ← identical pattern, new fields
│   ├── UI/
│   │   ├── SystemTrayIcon.cs         ← identical pattern
│   │   ├── SettingsWindow.xaml/.cs   ← identical pattern
│   │   └── NotificationManager.cs   ← identical
│   ├── App.xaml
│   └── App.xaml.cs
├── tests/
├── assets/
├── docs/
├── MsgImageExtractor.csproj
└── MsgImageExtractor.sln
```

**Framework**: .NET 8.0 WPF (matches heic-convert exactly)
**Core library**: [MsgReader](https://github.com/Sicos1977/MSGReader) — reads OLE2/CFB `.msg` binary structure without Outlook installed

The target user works in an ambient, always-running environment: `.msg` files arrive in a watched folder (from email exports, mail rule saves, or archival workflows), and images are automatically extracted without manual intervention.

### CLI Extension Path

The Core library (`ExtractionEngine`, `ExtractionQueue`, `Settings`) is deliberately UI-agnostic. Adding a CLI entry point requires no changes to Core:

```
MsgImageExtractor/
├── src/
│   ├── Core/           ← no changes needed
│   ├── UI/             ← tray app host
│   └── Cli/            ← future: Program.cs, CliRunner.cs
│       ├── Program.cs            entry point, arg parsing
│       └── CliRunner.cs          wraps ExtractionEngine directly
```

A future `MsgImageExtractor.Cli` project references the same `Core` assembly and calls `ExtractionEngine.ExtractAsync()` directly, bypassing the tray, queue, and watcher entirely. No architectural changes are required now to enable it later — just don't let UI concerns leak into Core.

---

## Component Mapping

### `ExtractionEngine.cs` ← `ConversionEngine.cs`

The workhorse. Receives a `.msg` file path, opens it via MsgReader, locates attachment streams, filters by type and size, writes images to the output path.

Key patterns carried over from `ConversionEngine`:

- Returns an immutable `ExtractionResult` record (see shape below)
- `SkipExisting` check before processing (see SkipExisting section below)
- `TryOpenFile()` single-probe lock check (see File Locking section below)
- `GetOutputPath()` respects `OutputStrategy` enum (`SameFolder` or `CustomFolder`)
- `CancellationToken` threaded throughout

**`OriginalFileAction` is not carried over.** Unlike HEIC conversion — where the output JPG is a full replacement for the source — extracting images from a `.msg` leaves the email body, metadata, sender, subject, and non-image attachments behind. Deleting or moving the source after extraction would silently destroy email data. The original `.msg` is always left untouched. No setting, no option.

**New behavior specific to MSG extraction:**

```
ExtractionEngine.ExtractAsync(msgPath)
  └── SkipExisting check — O(1) sentinel lookup; exits early, no file handle opened
  └── TryOpenFile() — fast-fail if locked (only reached if file will be processed)
  └── opens .msg via MsgReader
      └── iterates message.Attachments
          ├── filter: extension in [.jpg .jpeg .png .gif .bmp .heic .webp]
          ├── filter: size >= Settings.MinAttachmentSizeKb (default 10 KB)
          ├── filter: skip Content-ID inline images if Settings.ExtractInlineImages = false
          ├── write to output as [MsgFileName]_[AttachmentFileName] (with collision suffix)
          └── if RecurseEmbeddedMessages = true and attachment extension is .msg:
              └── open attachment bytes as nested MsgReader.Storage and recurse
                  (depth-capped; see RecurseEmbeddedMessages section below)
```

**`RecurseEmbeddedMessages` behavior:** When `RecurseEmbeddedMessages = true`, any attachment with a `.msg` extension is opened as a nested `MsgReader.Storage` and processed by the same extraction logic recursively. Images from embedded messages are written to the same output directory as top-level images. The output filename prefix uses the full parent chain: `{outerMsgName}_{embeddedMsgName}_{attachment}` (the `{msgname}` token resolves to the chain joined by `_`). Recursion is capped at **3 levels** of nesting. Embedded `.msg` attachments encountered at depth 3 are skipped and logged at `Warning` level. Sentinels are only written for the top-level `.msg` file — not for embedded messages.

#### `ExtractionResult` shape

One `.msg` produces N images, so the result record is:

```csharp
public record ExtractionResult(
    ExtractionFailureReason? FailureReason,  // null = success
    string SourcePath,
    IReadOnlyList<string> OutputPaths,        // one entry per extracted image
    int ExtractedCount,
    int DuplicateImageCount,                  // images skipped by DeduplicateByHash
    bool AlreadyProcessed,                   // true if .msg skipped by SkipExisting
    string? ErrorMessage,
    TimeSpan Duration
)
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
```

- `IsSuccess` is a computed property — it cannot be set independently of `FailureReason`, making contradictory states impossible.
- When `SkipExisting` fires: `AlreadyProcessed = true`, `OutputPaths` is empty, `ExtractedCount = 0`, `FailureReason` is `null` — a valid non-error result.

**`ExtractionFailureReason` trigger definitions:**

| Reason | Trigger |
|---|---|
| `PasswordProtected` | MsgReader throws on an encrypted or password-protected message |
| `FileInUse` | `TryOpenFile()` returns `false` (OS sharing violation) |
| `UnsupportedFormat` | File has `.msg` extension but MsgReader cannot recognise it as a valid OLE2/CFB container (e.g. renamed text file, corrupted message, pre-2000 format variant not supported by MsgReader) |
| `IoError` | Disk read error, path too long, permissions failure — OS-level I/O failure unrelated to file format |
| `UnexpectedError` | Any exception not matching the above categories |

---

### `ExtractionQueue.cs` ← `ConversionQueue.cs`

Channel-based worker pool — code is nearly identical to heic-convert. Key properties:

- Bounded `Channel<string>` with fixed capacity `1024`, `FullMode = DropWrite` — capacity is independent of worker count and represents the maximum number of files that can be queued ahead of processing at any moment
- Configurable `MaxConcurrentExtractions` (default 2, matches heic-convert's `MaxConcurrentConversions`)
- Pause/Resume (see Pause behaviour below) — `ExtractionQueue` exposes `BlockWorkers()` and `UnblockWorkers()` via `SemaphoreSlim`; it does not know about `FileWatcher`. The orchestrator (`App.xaml.cs`) handles both: Pause calls `_watcher.EnableRaisingEvents = false` then `_queue.BlockWorkers()`; Resume calls `_watcher.EnableRaisingEvents = true` then `_queue.UnblockWorkers()`. Files arriving during a pause are not seen and not queued — no drop, no loss. The user can use "Extract From Folder..." to catch up missed files if needed. **`BlockWorkers()` semantics:** acquires all permits of the `SemaphoreSlim` (one per `MaxConcurrentExtractions`), blocking until each in-flight worker finishes its current item and releases its permit. Workers are fully drained before `BlockWorkers()` returns. `UnblockWorkers()` releases all permits.
- Events:
  - `ExtractionCompleted` — payload: `ExtractionResult`. Fired after each `.msg` is fully processed (success or failure).
  - `QueueCountChanged` — payload: `int` (current approximate channel item count via `Channel.Reader.Count`). Fired after every `TryWrite` and after every dequeue. `PendingRetryCount` is a separate `int` property on `ExtractionQueue`, not carried in this event. The tray status reads both when refreshing its display string.

**Session counters** — `ExtractionQueue` tracks four counters for the current session:

| Counter | Unit | Increments on |
|---|---|---|
| `ExtractedCount` | Per image | Sum of `ExtractionResult.ExtractedCount` across all processed `.msg` files — counts images written, not files processed. The tray label "14 extracted" means 14 images. |
| `FailedCount` | Per `.msg` | `IoError`, `UnexpectedError`, or `FileInUse` after retry exhausted |
| `SkippedCount` | Per `.msg` | `PasswordProtected` or `UnsupportedFormat` — expected conditions, not processing errors |
| `DroppedCount` | Per `.msg` path | `TryWrite` returns `false` (channel full, `DropWrite` mode) |

"failed" and "skipped" are reported separately because skips are predictable conditions (unsupported or protected files), not processing errors.

**Zero-value counter suppression:** Counters with a value of zero are omitted from the tray status string. Only non-zero counters are shown. On a clean session before any files arrive, the status reads simply `"Monitoring since 09:32"`. Counters are added to the string as they become non-zero:

```
Monitoring since 09:32                                              ← clean session
Monitoring since 09:32 — 1 extracted                               ← first success
Monitoring since 09:32 — 14 extracted, 2 skipped                   ← no failures/drops
Monitoring since 09:32 — 14 extracted, 1 failed, 2 skipped         ← failure appears
Monitoring since 09:32 — 14 extracted, 1 failed, 2 skipped, 3 dropped  ← full set
```

Counter order in the string is fixed: extracted → failed → skipped → dropped → pending retry. A counter is never removed once it becomes non-zero for a session (it would jump from present to absent confusingly).

**First-drop notification:** When `DroppedCount` first reaches 1 for a session, a single tray balloon fires: `"Queue full — files are being dropped. Use Extract From Folder... to process missed files."` Subsequent drops in the same session do not fire additional balloons.

**Two deduplication layers — distinct purposes, not redundant:**

- **FileWatcher layer** (`ConcurrentDictionary` + 2-second window): suppresses duplicate `FileSystemWatcher` events fired by the OS for a single file arrival. Operates at event time, before the file enters the channel. Reduces the probability of duplicates reaching the channel but cannot guarantee it.
- **Queue layer** (`ConcurrentDictionary` of in-flight paths): the definitive guard against concurrent duplicate processing. Checked at **dequeue time**: when a worker dequeues a path, it calls `TryAdd(path, 0)`. Returns `true` — path is not in-flight; worker proceeds and removes the entry on completion. Returns `false` — path is already being processed by another worker (duplicate entry in channel); worker discards the item and dequeues the next one. Entry is removed on processing completion regardless of outcome.

**Image deduplication** (`DeduplicateByHash`): a `ConcurrentDictionary<string, byte>` of SHA-256 hashes owned by `ExtractionQueue` and injected into each `ExtractAsync` call. `TryAdd(hash, 0)` is the combined check-and-insert — atomic, no external lock needed. Workers that race on the same image hash: one gets `true` (writes), the other gets `false` (skips). Resets on app restart; clearable via Settings button (see below).

The "Extract From Folder..." one-shot batch operation uses the same session-level dedup dictionary as the file watcher. Images already extracted during the current monitoring session will not be re-extracted by a subsequent batch run, and vice versa.

**The 500ms debounce from heic-convert is not carried over.** `.msg` files arrive via move (single `Renamed` event) — no incremental-write event storm occurs. If testing against real Outlook export behaviour reveals spurious duplicate events, a short debounce (100ms max) can be reintroduced at the `FileWatcher` level only, not in the queue.

---

### `FileWatcher.cs`

Watches `*.msg` and `*.MSG`. Internal buffer: 64 KB. Subscribes to both `Created` and `Renamed` events.

**Event trigger note:** `.msg` files typically arrive via a file *move* (from Outlook's temp directory or a manual drag-and-drop), not a byte-by-byte write. A move fires a single `Renamed` event. Subscribing to both `Created` and `Renamed` catches both drop and move arrivals.

**2-second deduplication window:** `ConcurrentDictionary<string, DateTime>` keyed on full path. On each `Created`/`Renamed` event: if the path is already in the dictionary and `(DateTime.UtcNow - storedTime) < 2s`, discard the event. Otherwise add or update the entry with the current timestamp and forward the path to the channel. Entries are never explicitly removed — stale entries are overwritten on the next event for the same path and have no effect.

**Auto-restart on watcher error:** The `FileSystemWatcher.Error` event fires when the OS drops events (e.g. buffer overflow) or the watched path becomes inaccessible. On error: log the error at `Warning` level, call `Dispose()` on the current watcher instance, create a new `FileSystemWatcher` with identical configuration (path, filter, `IncludeSubdirectories`, buffer size, subscribed events), and assign it in place. If the watched folder no longer exists at restart time, log at `Warning` and retry after 10 seconds (up to 3 times); if the folder is still missing after 3 attempts, log at `Error` and leave the watcher stopped until the app is restarted.

---

### `Settings.cs`

Persists to `%APPDATA%\MsgImageExtractor\settings.json`. Inherits heic-convert settings fields plus new fields below.

| New Field | Type | Default | Purpose |
|---|---|---|---|
| `WatchedFolder` | `string` | `""` | The folder monitored for incoming `.msg` files. Empty = not configured; triggers first-run prompt |
| `MinAttachmentSizeKb` | `int` | `10` | Skip images smaller than this (filters out signature icons). Valid range: 1–10,000 KB. The UI enforces this range; values outside it are clamped on load. |
| `ExtractInlineImages` | `bool` | `false` | Include CID-referenced inline images from HTML body |
| `RecurseEmbeddedMessages` | `bool` | `false` | Recurse into nested `.msg` attachments |
| `DeduplicateByHash` | `bool` | `true` | Skip images with identical SHA-256 across the entire session — see Deduplication section |

Retained from heic-convert:

```csharp
public OutputStrategy OutputStrategy { get; set; } = OutputStrategy.SameFolder;
public string CustomFolder { get; set; } = string.Empty;
public int MaxConcurrentExtractions { get; set; } = 2;  // 1–8, configurable via slider
public bool SkipExisting { get; set; } = true;
public bool IncludeSubdirectories { get; set; } = true;
public string FileNamingPattern { get; set; } = "{msgname}_{attachment}";
```

`FileNamingPattern` is not surfaced in `SettingsWindow`. It is an advanced setting, editable by modifying `settings.json` directly. The default `{msgname}_{attachment}` covers expected use cases. A future Settings UI revision may expose this if user demand warrants it.

**Token definitions:**

| Token | Resolves to |
|---|---|
| `{msgname}` | `.msg` filename **without** the `.msg` extension (e.g. `Invoice 2024-03`) |
| `{attachment}` | Attachment filename **including** its own extension (e.g. `photo.jpg`) |

So the default pattern `{msgname}_{attachment}` produces `Invoice 2024-03_photo.jpg`.

Illegal filesystem characters (`\ / : * ? " < > |`) in either token are replaced with `_` before the name is assembled. Path separators within attachment filenames are also replaced with `_`. When `RecurseEmbeddedMessages = true`, the `{msgname}` token for images from nested messages resolves to the full parent chain joined by `_` (e.g. `OuterMsg_InnerMsg`), ensuring output filenames remain flat and unique.

`IncludeSubdirectories` is not surfaced in `SettingsWindow`. It is an advanced setting, editable by modifying `settings.json` directly. The default `true` is appropriate for the majority of users. A future Settings UI revision may expose this if user demand warrants it.

**`OriginalFileAction` is removed entirely** — original files are always kept.

**`OutputStrategy`** has two options: `SameFolder` (default) and `CustomFolder`. `MirrorStructure` is not included — the use case (mirroring an email archive tree) does not map naturally to image extraction and adds complexity without a clear user story.

**`OutputStrategy` default is `SameFolder`**, matching heic-convert. Users can change this in Settings.

The `CustomFolder` path is always persisted in `settings.json` regardless of the current `OutputStrategy` value. When the user switches to `SameFolder`, the output folder picker is hidden but the stored path is retained. Switching back to `CustomFolder` restores the previously entered path — no data loss, no re-entry required.

`MaxConcurrentExtractions` is surfaced in `SettingsWindow` as a slider (range 1–8, default 2). MSG extraction is I/O-bound (disk reads + OLE2 stream parsing), so the same 1–8 range is appropriate.

---

### `SystemTrayIcon.cs`

Same structure as heic-convert. Menu items:

- Status (read-only): `"Monitoring since 09:32 — 14 extracted, 2 skipped, 3 dropped"` (zero-value counters omitted per the zero-suppression rule)
- When paused: `"Paused since 09:45 — 14 extracted, 2 skipped, 3 dropped"` — `SystemTrayIcon` holds a `PausedSince` timestamp, set on Pause and cleared on Resume
- When a `FileInUse` retry is pending: `"Monitoring since 09:32 — 14 extracted, 2 skipped, 3 dropped, 1 pending retry"`
- Pause / Resume toggle
- **Extract From Folder...** (equivalent of "Convert Existing Files")
- Settings
- Exit

### `NotificationManager.cs`

All tray balloon notifications are routed through `NotificationManager` — `SystemTrayIcon` does not fire balloons directly.

**API:** Uses `NotifyIcon.ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon)` (WinForms `NotifyIcon`, already present in the tray app — no WinRT/UWP dependency). Balloon title is always `"MSG Image Extractor"`. `timeout` is `3000` ms for `ToolTipIcon.Info` notifications and `5000` ms for `ToolTipIcon.Warning` notifications. All four notifications below use `Warning`.

Notifications fired by this app:

| Trigger | Message |
|---|---|
| `DroppedCount` first reaches 1 (session) | `"Queue full — files are being dropped. Use Extract From Folder... to process missed files."` |
| Dedup suspended (500k entry limit) | `"Image deduplication suspended — cache limit reached. Open Settings to clear the cache and resume."` |
| `FileInUse` retry exhausted | `"[filename] — could not access file (in use)"` |
| `PasswordProtected` | `"[filename] — could not extract: message is password-protected"` |

The `PasswordProtected` notification is included here because it is a predictable condition that merits user awareness even though it is counted as "skipped" rather than "failed."

### `Logger.cs`

Plain file logger — no external dependency.

- **File path:** `%APPDATA%\MsgImageExtractor\logs\msgextractor-{yyyy-MM-dd}.log` (one file per calendar day; old log files are not deleted)
- **Format:** `{HH:mm:ss.fff} [{LEVEL}] {message}` — e.g. `09:32:01.452 [WARN] FileWatcher error: buffer overflow — restarting watcher`
- **Levels:** `Info`, `Warning`, `Error` (no external logging framework)
- **Implementation:** `StreamWriter` with `AutoFlush = true`, wrapped in a `lock` for thread safety. Opened on first write; a new `StreamWriter` is opened when the date rolls over.
- **Flush on shutdown:** `Logger.Flush()` is called during the shutdown sequence (step 6) to ensure all buffered entries reach disk before the process exits.

---

### `SettingsWindow.xaml`

WPF form mirroring heic-convert's SettingsWindow. Controls, in order:

- Folder picker: **Watched folder** (required; app does not monitor until set)
- Dropdown: **Output strategy** — Same Folder / Custom Folder
- Folder picker: **Output folder** (only shown when `OutputStrategy = CustomFolder`)
- Slider: **Max concurrent extractions** (1–8)
- Numeric input: **Minimum attachment size** (KB) — range 1–10,000, default 10
- Checkbox: **Deduplicate identical images** (SHA-256, on by default) — label changes to "Deduplicate identical images ⚠ suspended — cache full" if the session dedup limit is reached
- Checkbox: **Extract inline images** (CID references from HTML body)
- Checkbox: **Recurse into embedded messages**
- Button: **Reset processed file history** — deletes all sentinel files from `%APPDATA%\...\processed\`; next run will re-extract all `.msg` files
- Button: **Clear image dedup cache** — clears the in-memory hash set for the current session; subsequent images will no longer be skipped as duplicates until the set rebuilds

These two buttons are distinct operations with different consequences and are labelled accordingly.

### Settings persistence

Settings are applied and persisted immediately on change — there is no Save or Cancel button. Each control writes its value to the in-memory settings object on change; the settings object flushes to `settings.json` on every write. Closing the window is always safe; no pending changes are lost. Changes that affect an active session (e.g., adjusting `MaxConcurrentExtractions`) take effect immediately without a restart.

**Flush failure:** If the `settings.json` write fails (permissions error, disk full, etc.), the error is logged at `Warning` level and silently swallowed. In-memory settings remain correct and continue to govern the active session. No user-visible notification is shown for transient write failures — the next successful write will persist the current in-memory state.

---

### `Extract From Folder...`

One-shot batch operation available from the tray menu. Allows the user to process `.msg` files that arrived while the app was not running, was paused, or were dropped due to a full queue.

**Behaviour:**

- Opens a folder picker. The user selects any folder (not limited to the currently watched folder).
- Enumerates `*.msg` files in the selected folder, honouring `IncludeSubdirectories`.
- Each file is submitted to the same `ExtractionQueue` used by the file watcher — it runs concurrently with live monitoring if monitoring is active.
- `SkipExisting` applies: files with a valid sentinel are skipped without processing.
- Uses the same session-level `DeduplicateByHash` dictionary as the file watcher. Images extracted during monitoring will not be re-extracted by a batch run, and vice versa.
- On completion, a tray balloon fires: `"Batch complete — N images extracted from M files."` (where M = files processed, N = images written). If all files were skipped by `SkipExisting`, the message is `"Batch complete — all files already processed."`.
- **Unconfigured state + `SameFolder`:** If `OutputStrategy = SameFolder` and `WatchedFolder` is empty (app not yet configured), "Extract From Folder..." uses the **selected batch folder itself** as the output location — images are written alongside the `.msg` files in the folder the user chose. This is the only case where `SameFolder` resolves to the batch folder rather than each `.msg` file's own directory.

**Status during batch:** The tray status string updates in real time as batch items are processed — the same counters increment. There is no separate "batch mode" status.

---

## First-Run Experience

On first launch, if `WatchedFolder` is empty, `SettingsWindow` opens automatically with two controls highlighted:

1. **Watched folder** — folder picker with prompt: "Choose a folder to monitor for .msg files."
2. **Output strategy** — dropdown defaulting to `SameFolder`, shown immediately beneath the watched folder picker with a contextual note:
   - If `SameFolder` is selected: "Images will appear alongside your .msg files."
   - If `CustomFolder` is selected: the output folder picker appears inline.

The user makes an informed output choice on first run without a separate Settings visit. The app does not begin monitoring until a watched folder is selected. All other settings have usable defaults.

**Close without configuring:** If the user closes `SettingsWindow` without selecting a watched folder, the app remains running but idle. The tray status item reads `"Not configured — click Settings to set up"` instead of the normal monitoring string. The Pause/Resume menu item is hidden (there is nothing to pause). "Extract From Folder..." remains available. The tray icon uses a distinct "not configured" icon or badge to signal the idle state. The app does not reopen Settings automatically — the user must click Settings to proceed.

---

## Design Decisions — Detail

### SkipExisting

`SkipExisting = true` uses a sentinel file strategy. On successful extraction, a small marker file is written to:

```
%APPDATA%\MsgImageExtractor\processed\<SHA-256 of full source path>.sentinel
```

The sentinel contains the full absolute source path as its content, which enables source-existence pruning on startup (see below). `SkipExisting` checks for the presence of this marker before processing: `File.Exists(sentinelPath)`. This is:

- O(1) per file — single `File.Exists()` call, no directory scan
- Unambiguous — the marker either exists or it doesn't
- Collision-free — keyed on SHA-256 of the full source path, not just the filename; two files named `report.msg` in different directories produce different sentinels
- Never visible to the user — stored in `%APPDATA%`, not alongside source or output files

**Sentinel write condition:** A sentinel is written whenever a `.msg` is processed without a hard error — i.e., `IsSuccess = true` — regardless of how many images were extracted. A text-only email that produces `ExtractedCount = 0` is still "processed"; writing a sentinel prevents re-processing it on every future run. `ExtractedCount = 0` is a valid, non-error outcome and does not suppress sentinel creation.

**Sentinel lifecycle — source-existence pruning:**

Sentinels are pruned on each app startup. The startup sequence scans `%APPDATA%\...\processed\`, reads the stored source path from each sentinel's content, and deletes any sentinel whose source `.msg` no longer exists on disk. The `processed\` folder is therefore bounded by the number of `.msg` files the user currently has on disk, not all files ever processed.

No TTL (time-based expiry would allow re-extraction of legitimately processed files) and no count cap (count-based eviction would silently invalidate valid sentinels) are used.

If the user moves their watched folder to a different path, sentinels are orphaned (stored paths no longer match) and all files in the new location will be extracted once as fresh. This is expected and acceptable.

"Reset processed file history" in Settings performs an immediate full clear of `%APPDATA%\...\processed\` for cases where the user explicitly wants to re-extract everything.

---

### Deduplication (`DeduplicateByHash`)

**Scope: session-level (app lifetime), not per-message.**

The high-value case is cross-message: the same company logo, email signature, or decoration appears in every one of 500 `.msg` files in a batch. Per-message dedup catches the rare case of a single email embedding the same image twice.

Implementation: `ExtractionQueue` owns a `ConcurrentDictionary<string, byte>` of SHA-256 hashes, injected into each `ExtractAsync` call. `TryAdd(hash, 0)` is the atomic check-and-insert. Resets on app restart. Clearable via the "Clear image dedup cache" Settings button.

**Memory**: SHA-256 raw output is 32 bytes. Stored as a 64-character hex string in .NET (UTF-16), the string alone occupies 128 bytes plus ~20 bytes of object header. With `ConcurrentDictionary` node overhead (~50 bytes per slot), realistic cost is approximately 200 bytes per entry. 100,000 unique images ≈ 20 MB; 500,000 entries ≈ 100 MB. If the dictionary grows beyond 500,000 entries, dedup is suspended for the remainder of the session. When suspension triggers:

1. A tray balloon notification fires: "Image deduplication suspended — cache limit reached. Open Settings to clear the cache and resume."
2. The `SettingsWindow` dedup checkbox label changes from "Deduplicate identical images" to "Deduplicate identical images ⚠ suspended — cache full" while suspension is active. The checkbox remains checked (the user's preference has not changed).

Clicking "Clear image dedup cache" clears the dictionary, lifts the suspension, and removes the warning label.

**Cross-session interaction with `SkipExisting`:** `DeduplicateByHash` is session-scoped and cleared on restart. Cross-session duplicate prevention is handled entirely by `SkipExisting` at the `.msg` level — a sentinel means "all images from this file were already extracted in a previous session; skip without reading." The dedup dictionary does not need to survive restarts because the sentinel provides a stronger guarantee: if a `.msg` is skipped, none of its images are read and none would be re-extracted, regardless of the hash dictionary state.

---

### File Locking

`WaitForFileReady` from heic-convert is replaced with a single-probe function:

```csharp
bool TryOpenFile(string path)
// Attempts to open the file with FileShare.None.
// Returns true if successful (file is accessible), false on IOException (file is locked).
// No polling loop, no wait parameter.
```

**FSW callback — non-blocking channel write:** `FileSystemWatcher` events fire on .NET thread pool threads. The FSW event handler uses `Channel.Writer.TryWrite()` — non-blocking, returns immediately. If the channel is full (`DropWrite` mode), the incoming path is dropped and logged as a warning. Dropped files are recoverable via "Extract From Folder...". Thread pool threads are never blocked.

On `TryOpenFile` returning `false`: `ExtractionEngine.ExtractAsync` returns `ExtractionResult { FailureReason = FileInUse }`. The `ExtractionQueue` **worker loop** — not `ExtractionEngine` — detects this result and schedules the retry. `ExtractionEngine` has no access to `_retryChannel` and no knowledge of retry logic.

The retry is scheduled via a dedicated `RetryChannel` owned by `ExtractionQueue`:

```csharp
// Inside ExtractionQueue worker loop, after receiving FileInUse result:
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(30), _shutdownCts.Token);
    _retryChannel.Writer.TryWrite(new RetryEntry(path, attempt: 1));
}, _shutdownCts.Token);
```

`ExtractionQueue` owns a `CancellationTokenSource _shutdownCts` for the current session. `_retryChannel` is an **unbounded** `Channel<RetryEntry>` (the number of pending retries is bounded by `MaxConcurrentExtractions` — always a small set). A dedicated retry-drain worker reads from `_retryChannel` and re-submits paths to the main channel. Using `_shutdownCts.Token` ensures all pending retries are cancelled cleanly on app shutdown — no fire-and-forget tasks outliving the process.

`RetryEntry` holds the file path and attempt number. Attempt 1 re-queues to the main channel. If `TryOpenFile` fails again on attempt 1: `FailedCount` increments, a tray notification fires `"[filename] — could not access file (in use)"`, and no further retry is scheduled.

`PendingRetryCount` in the tray status is the current count of items awaiting drain from `_retryChannel`.

While any file is awaiting its 30-second retry, the tray status reflects it:
```
Monitoring since 09:32 — 14 extracted, 0 failed, 2 skipped, 3 dropped, 1 pending retry
```

---

### Naming Collision

Output filenames follow `{msgname}_{attachment}`. On collision, a counter suffix is inserted between the stem and the extension:

```
invoice_photo.jpg        ← first write
invoice_photo_2.jpg      ← collision: strip extension, append _2, re-add extension
invoice_photo_3.jpg      ← second collision
```

The counter is never appended after the extension (`invoice_photo.jpg_2` is incorrect). The stem is everything before the last `.` in the resolved output filename.

---

## Decision Log

| Question | Decision | Rationale |
|---|---|---|
| CLI vs. tray app? | **Tray app** with Core library structured for CLI extension | Initial brief said CLI; heic-convert reference architecture is a tray app; ambient monitoring is the primary use case; CLI entry point requires no Core changes |
| Minimum file size threshold? | **10 KB default, user-configurable** | Signature icons are typically 1–5 KB; 10 KB eliminates most false positives without risk to real photos |
| Recurse into embedded `.msg`? | **Off by default** (`RecurseEmbeddedMessages = false`) | Avoids unexpected depth/volume; power users can enable |
| Deduplicate identical images? | **On by default, session scope** | Same signature image repeats across every message in a batch; `ConcurrentDictionary` gives safe cross-worker check-and-insert |
| Inline images (CID refs)? | **Off by default** (`ExtractInlineImages = false`) | Inline images are usually decorative; opt-in |
| Naming collision? | Counter suffix before extension | `invoice_photo.jpg` → `invoice_photo_2.jpg`; no silent overwrites |
| OriginalFileAction? | **Removed** | Deleting/moving a `.msg` discards email body and metadata; always keep original |
| SkipExisting check? | **Sentinel file in `%APPDATA%`** | O(1), collision-free, invisible to user, self-pruning |
| Sentinel lifetime? | **Source-existence pruning on startup** | No TTL, no count cap; bounded by files currently on disk |
| OutputStrategy? | **SameFolder / CustomFolder only** | `MirrorStructure` dropped; no clear user story for email archive mirroring |
| Default OutputStrategy? | **SameFolder** (configurable) | Matches heic-convert default |
| Debounce? | **Removed from queue**; 2s watcher dedup window retained | `.msg` arrives via move (single event); reintroduce at FileWatcher level only if testing shows need |
| File lock handling? | **`TryOpenFile()` single probe + one 30s retry via `RetryChannel` + `_shutdownCts`** | Tracked, cancellable retry; no fire-and-forget tasks; `PendingRetryCount` drives tray status |
| Password-protected files? | **`PasswordProtected` failure reason** | Distinct from errors; surfaced clearly in tray and log |
| Channel capacity? | **Fixed at 1,024, `FullMode = DropWrite`** | Decoupled from worker count; dropped files logged and recoverable via batch; no thread pool blocking |
| WatchedFolder first-run? | **Block monitoring until folder is set; show output strategy inline** | User makes informed output choice on first run |
| `CustomFolder` path on strategy switch? | **Always persisted; restored on switch back** | No data loss; no re-entry required |
| `Success` field? | **Removed; replaced by `IsSuccess` computed property** | `FailureReason == null` is the canonical success signal; contradictory states impossible |
| `SkippedCount`? | **Split into `DuplicateImageCount` + `bool AlreadyProcessed`** | Different causes, different tray copy; `AlreadyProcessed` is binary so typed as `bool` |
| Tray counter definitions? | **`extracted` = images written (per-image sum); `failed`/`skipped`/`dropped` = per-msg** | "14 extracted" means 14 images; skips are expected conditions not errors; dropped files distinct from failures |
| Pause behaviour? | **Orchestrator calls `_watcher.EnableRaisingEvents = false` then `_queue.BlockWorkers()`**; queue exposes `BlockWorkers`/`UnblockWorkers`, does not reference `FileWatcher` | Clean separation; files during pause are not seen; status shows `"Paused since HH:mm"` |
| Dedup suspension visibility? | **Tray notification + checkbox warning label** | User opted in; silent suspension is a broken contract |
| `Extract From Folder...` dedup? | **Shares session dedup dictionary with watcher** | Consistent dedup across both entry points |
| Execution order? | **`SkipExisting` before `TryOpenFile()`** | No file handle opened for already-processed files |
| `UnsupportedFormat`? | **Defined: non-OLE2 file with `.msg` extension** | Distinct from `IoError`; MsgReader parse failure |
| `FileNamingPattern`? | **Advanced setting; JSON-only, not in UI** | Default covers expected cases; UI exposure deferred |
| `IncludeSubdirectories`? | **Advanced setting; JSON-only, not in UI; default `true`** | Default appropriate for most users; same pattern as `FileNamingPattern` |
| Shutdown/drain? | **Finish current in-flight items; abandon queued-but-not-dequeued; cancel pending retries via `_shutdownCts`** | In-flight sentinels are written; abandoned files reprocess on next run; no data loss |
| Single-instance? | **Named system mutex; second launch exits silently** | Prevents conflicting watchers, settings writes, and sentinel directory access |

---

### Startup Sequence

On launch, `App.xaml.cs` executes the following steps in order:

1. **Single-instance check:** Attempt to acquire a named system `Mutex` (`"Global\MsgImageExtractorSingleInstance"`). If the mutex is already held by another process, exit immediately with no UI shown.
2. **Directory initialisation:** Ensure `%APPDATA%\MsgImageExtractor\`, `logs\`, and `processed\` subdirectories exist. Create any that are missing.
3. **Settings load:** Deserialise `settings.json`. If the file is missing or unparseable (corrupt JSON, schema mismatch), use all defaults and write a fresh `settings.json`. Clamp any out-of-range field values (e.g. `MinAttachmentSizeKb` outside 1–10,000) before the settings object is used.
4. **Sentinel pruning:** Scan `%APPDATA%\MsgImageExtractor\processed\`, read the stored source path from each sentinel's content, and delete any sentinel whose source `.msg` no longer exists on disk. This runs **foreground** — the watcher does not start until pruning completes, preventing a stale sentinel from causing a missed extraction.
5. **First-run check:** If `WatchedFolder` is empty, open `SettingsWindow` in first-run mode (see First-Run Experience). The watcher is **not** started. Return; the app waits for the user to configure a folder.
6. **Queue and watcher start:** If `WatchedFolder` is set, initialise `ExtractionQueue` (start worker loops and retry-drain worker), then start `FileWatcher`.
7. **Tray icon shown.**

---

### Shutdown Sequence

On exit (tray → Exit, or system shutdown):

1. `_watcher.EnableRaisingEvents = false` — no new paths are enqueued.
2. `_shutdownCts.Cancel()` — all pending retry `Task.Delay` calls are cancelled immediately; the retry-drain worker exits its read loop.
3. `ExtractionQueue` completes any item currently being processed by a worker (each worker checks the cancellation token between items, not mid-item). In-flight extractions are allowed to finish; their sentinels are written normally.
4. Workers exit after their current item completes. No queued-but-not-yet-dequeued items are processed — they are abandoned. Abandoned files have no sentinel and will be processed on next startup (if still present) or recoverable via "Extract From Folder...".
5. Settings are already persisted (written on every change); no flush needed at shutdown.
6. `Logger` flushes any buffered entries.

**Mid-extraction shutdown:** If the app is killed hard (Task Manager, power loss), a `.msg` whose extraction was in progress has no sentinel written. On next startup, that file will be processed again from the beginning — the partial output files (if any were written) will be overwritten by the naming collision logic or left as orphans if the msg no longer exists. This is acceptable: images are never deleted, only potentially duplicated with a suffix.

---

## What Is NOT Carried Over from heic-convert

- **JPEG quality setting** — not applicable; images are extracted as-is, not transcoded
- **AutoOrient (EXIF rotation)** — not applicable; images extracted verbatim
- **ImageMagick / Magick.NET dependency** — replaced by MsgReader NuGet package
- **`OriginalFileAction`** — removed; original `.msg` is always preserved
- **500ms queue debounce** — removed; not applicable to move-based file arrival
- **`MirrorStructure` output strategy** — removed; no clear user story for this app
- **`WaitForFileReady` polling loop** — replaced by `TryOpenFile()` single probe

---

## Deployment

Identical to heic-convert:

```
dotnet publish -c Release -r win-x64 --self-contained true
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Produces a single `MsgImageExtractor.exe`. No .NET runtime or Outlook installation required on target machine.

---

## Key Risks

- **MsgReader coverage**: MsgReader handles the majority of `.msg` files but edge cases exist (non-standard attachment storage, very old Outlook formats). Need to test against a real corpus.
- **OLE2 large-file performance**: Very large `.msg` files (e.g., 50 MB+ with video attachments) will consume significant memory during stream extraction. The size filter helps but does not cap memory use.
- **Tray lifetime on long batch runs**: If a user drops 1,000 `.msg` files at once and the channel (capacity 1,024) fills, new arrivals are dropped with a log warning. Dropped files are recoverable via "Extract From Folder...". No thread pool threads are blocked.
- **Dedup dictionary growth**: Session-level `ConcurrentDictionary` can grow large on very long runs. Log a warning and suspend dedup at 500,000 entries (≈ 100 MB at ~200 bytes per entry).
- **Startup pruning cost**: Pruning orphaned sentinels on startup is O(n) over the `processed\` folder. For users with extremely large archives (100k+ processed files) this could add a noticeable startup delay.

  **Default: foreground pruning.** This is always correct — the watcher does not start until pruning completes, so no stale sentinel can cause a missed extraction.

  **Background pruning trade-off**: if moved to a background thread to avoid blocking the tray icon, a correctness risk is introduced. If a `.msg` file arrives at a path whose stale sentinel has not yet been pruned, `SkipExisting` finds the stale sentinel and silently skips the file. The file is never extracted. This is a silent miss with no user-visible indication. Background pruning should only be considered if profiling confirms a real startup delay problem, and only with explicit documentation of the missed-extraction risk.