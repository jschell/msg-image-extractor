# MsgImageExtractor

## Commands

| Task | Command |
|---|---|
| Build | `dotnet build MsgImageExtractor.sln -c Release` |
| Test | `dotnet test MsgImageExtractor.sln -c Release` |
| Publish | `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` |

## Architecture

- `src/Core/` — UI-agnostic extraction logic
- `src/UI/` — WPF system tray application
- Framework: .NET 8.0 WPF, x64
- Core library: MsgReader NuGet package
