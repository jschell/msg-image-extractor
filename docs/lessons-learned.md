# Lessons Learned

Issues encountered across all development sessions, in roughly chronological order. Each entry names the root cause, what went wrong, and the fix applied.

---

## 1. Tests leaking into main project compilation

**Symptom:** xunit types (`ITestOutputHelper`, `[Fact]`, etc.) resolved in `MsgImageExtractor.csproj` build because the wildcard `<Compile Include="**\*.cs" />` pulled in files under `tests/`.

**Root cause:** The main `.csproj` used an implicit glob that did not exclude the `tests/` subdirectory. Both project files sit at the repo root, so the tests folder was inside the glob's scope.

**Fix:** Add explicit `Remove` items to `MsgImageExtractor.csproj`:
```xml
<Compile Remove="tests\**" />
<EmbeddedResource Remove="tests\**" />
<None Remove="tests\**" />
```

---

## 2. CS5001: No Main entry point (missing ApplicationDefinition)

**Symptom:** `error CS5001: Program does not contain a static 'Main' method suitable for an entry point`.

**Root cause:** `src/App.xaml` was listed as a plain `<None>` item. WPF generates the `Main` method from the `App.xaml` code-behind only when the file has `BuildAction = ApplicationDefinition`.

**Fix:**
```xml
<ApplicationDefinition Include="src\App.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</ApplicationDefinition>
```

---

## 3. CS0234: MRPasswordException does not exist in MsgReader v6

**Symptom:** `error CS0234: The type or namespace name 'MRPasswordException' does not exist in the namespace 'MsgReader'`.

**Root cause:** MsgReader v6 removed `MRPasswordException`. Password-protected files now throw `MRFileTypeNotSupported` with a descriptive message containing "password" or "encrypt".

**Fix:** Replace the non-existent catch:
```csharp
// Before (broken):
catch (MsgReader.Exceptions.MRPasswordException) { ... }

// After:
catch (MsgReader.Exceptions.MRFileTypeNotSupported ex)
    when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
       || ex.Message.Contains("encrypt",  StringComparison.OrdinalIgnoreCase))
{ ... }
```

---

## 4. CS1988: Illegal use of `ref` parameter in `async` method

**Symptom:** `error CS1988: Async methods cannot have ref or out parameters`.

**Root cause:** `ExtractFromMessageAsync` accumulated a duplicate count via a `ref int duplicates` parameter — not legal on `async` methods.

**Fix:** Return the duplicate count as part of the method's return value (`Task<int>`) instead of using a `ref` parameter.

---

## 5. Missing `using System.IO` in source and test files

**Symptom:** `File`, `Path`, `Directory` etc. unresolved across multiple source and test files.

**Root cause:** The project does not enable `<ImplicitUsings>enable</ImplicitUsings>`, so standard namespaces must be added explicitly. The initial scaffold omitted `using System.IO` throughout.

**Fix:** Added `using System.IO;` explicitly to every affected file in `src/` and `tests/`.

---

## 6. Platform mismatch: test DLL not found with `--no-build`

**Symptom:**
```
Test source file 'tests\bin\Release\net8.0-windows\MsgImageExtractor.Tests.dll' not found.
```

**Root cause:** The solution only defines `Release|x64` and `Debug|x64` configurations. When `dotnet build` runs without `/p:Platform=x64`, the build succeeds but places output at:
```
tests\bin\x64\Release\net8.0-windows\MsgImageExtractor.Tests.dll
```
A subsequent `dotnet test --no-build --configuration Release` without `/p:Platform=x64` looks at the wrong path (`tests\bin\Release\...`) and finds nothing.

**Fix:** Add `/p:Platform=x64` to every `dotnet restore`, `dotnet build`, and `dotnet test` call in both `ci.yml` and `release.yml`.

---

## 7. `InternalsVisibleTo` required for test access to internal helpers

**Symptom:** 74 test-project errors: `FileWatcher.SimulateFileEvent` and `ExtractionQueue.TestFillAndCountDrops` were inaccessible.

**Root cause:** Both helpers were marked `internal` (correct for production), but the test assembly could not see them without an explicit attribute.

**Fix:** Add to `MsgImageExtractor.csproj`:
```xml
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
  <_Parameter1>MsgImageExtractor.Tests</_Parameter1>
</AssemblyAttribute>
```

---

## 8. Logger: file-share conflict with test reads

**Symptom:** Four Logger tests failed with `IOException: The process cannot access the file … because it is being used by another process`.

**Root cause (first attempt):** The initial `StreamWriter` was opened with default exclusive access. Even after switching to `FileShare.ReadWrite`, `File.ReadAllText` in tests opens with `FileShare.Read` (no write sharing), which still conflicts with a held write handle.

**Root cause (second attempt):** Opening the `StreamWriter` with `FileShare.Read` still holds the file open; `File.ReadAllText` (which defaults to `FileShare.Read`, no write) conflicts.

**Fix:** Eliminate the persistent handle entirely — open the file, append the line, and close it on every `Write()` call using `File.AppendAllText`. No handle is held between writes.

---

## 9. `Channel.Reader.Count` not supported on `SingleConsumerUnboundedChannel`

**Symptom:** `NotSupportedException` when reading `PendingRetryCount`.

**Root cause:** `Channel.CreateUnbounded<T>(new() { SingleReader = true })` creates a `SingleConsumerUnboundedChannel` internally. That implementation does not override `Reader.Count`, so it throws `NotSupportedException`.

**Fix:** Track the count manually with a `long _pendingRetryCount` field, incremented in `ScheduleRetry` and decremented in `RetryDrainLoopAsync` and on cancellation. Expose it as a property.

---

## 10. `BoundedChannelFullMode.DropWrite` drops are undetectable via `TryWrite` return value

**Symptom:** Drop-counting tests always reported 0 drops even when the channel was full.

**Root cause:** `BoundedChannelFullMode.DropWrite` silently discards the item and returns `true` from `TryWrite`, making drops undetectable from the call site.

**Fix:** Switch to `BoundedChannelFullMode.Wait`. `TryWrite` returns `false` when the channel is full under `Wait` mode, allowing callers to detect and count the rejection.

---

## 11. `OperationCanceledException` propagating out of `StopAsync`

**Symptom:** Six tests calling `StopAsync()` failed with `OperationCanceledException` propagating upward.

**Root cause:** When `StopAsync` cancels the `CancellationTokenSource`, the `await foreach` loop in `WorkerLoopAsync` throws `OperationCanceledException`. This was not caught, so `Task.WhenAll` in `StopAsync` re-threw it to the caller.

**Fix:** Wrap the entire `await foreach` body in `try { … } catch (OperationCanceledException) { break; }` so cancellation is treated as a clean shutdown signal.

---

## 12. `GITHUB_TOKEN` silently suppresses release workflow re-trigger

**Symptom:** Release workflow never ran after the version-bump commit was pushed.

**Root cause:** GitHub's anti-loop protection silently suppresses `push` and `tag` events whose origin commit was created by `GITHUB_TOKEN`. The `check-release` job pushes a version-bump commit; if that push uses `GITHUB_TOKEN`, the `release.yml` workflow will not be triggered again.

**Fix:** Create a fine-grained PAT (`RELEASE_TOKEN`) with *Contents: read/write* on the repository. Use it for both the checkout and the `git push` in `check-release`, and for `softprops/action-gh-release` in `build-and-release`.

---

## 13. Release workflow only attached the zip, not the bare exe

**Symptom:** GitHub Releases page only offered a zip download; users had to unzip to get the exe.

**Root cause:** The original `release.yml` created a zip archive of the publish directory and attached only that. The bare `.exe` was not staged or listed in the release `files:` block.

**Fix:**
1. Copy `publish\MsgImageExtractor.exe` → `MsgImageExtractor-X.Y.Z-win-x64.exe`.
2. Hash both the `.exe` and the `.zip` into `checksums.txt`.
3. List the `.exe` first in `files:` so it is the most prominent asset.
