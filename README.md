# MsgImageExtractor

Windows system tray utility that monitors folders for Outlook `.msg` files and extracts all image attachments automatically.

## Download

Grab the latest build from the [Releases](../../releases/latest) page:

| File | Description |
|---|---|
| `MsgImageExtractor-X.Y.Z-win-x64.exe` | Standalone executable — just run it, no install needed |
| `MsgImageExtractor-X.Y.Z-win-x64.zip` | Same executable inside a zip archive |
| `checksums.txt` | SHA256 hashes for both files |

No .NET runtime and no Outlook installation required.

**Verify the download (optional):**
```powershell
# In the folder where you saved the file:
(Get-FileHash .\MsgImageExtractor-X.Y.Z-win-x64.exe -Algorithm SHA256).Hash
# Compare to the value in checksums.txt
```

## Usage

Double-click `MsgImageExtractor.exe` — the app starts in the system tray.

| Action | How |
|---|---|
| First run | Settings window opens automatically; set your watched folder |
| Set watched folder | Right-click tray icon → Settings |
| Batch extract | Right-click tray icon → Extract From Folder… |
| Pause / Resume | Right-click tray icon → Pause / Resume |
| Exit | Right-click tray icon → Exit |

Images are extracted **alongside** `.msg` files by default (*SameFolder* mode). You can switch to a fixed output directory in Settings.

## Build from source

```
dotnet build MsgImageExtractor.sln -c Release
```

**Publish self-contained single-file exe:**
```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

**Run tests:**
```
dotnet test MsgImageExtractor.sln -c Release
```

Requires .NET 8 SDK and Windows (WPF).

## License

MIT
