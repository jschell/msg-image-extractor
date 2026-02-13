# MsgImageExtractor

## Commands

| Task | Command |
|---|---|
| Build | `dotnet build MsgImageExtractor.sln -c Release` |
| Test | `dotnet test MsgImageExtractor.sln -c Release` |
| Publish | `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` |

## Architecture

- `src/Core/` — UI-agnostic: ExtractionEngine, ExtractionQueue, FileWatcher, Logger, Settings
- `src/UI/` — WPF tray app: SystemTrayIcon, SettingsWindow, NotificationManager
- `src/App.xaml.cs` — orchestrator: startup/shutdown sequence
- `tests/` — unit tests
- Framework: .NET 8.0 WPF, x64
- Core library: MsgReader NuGet package
