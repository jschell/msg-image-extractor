# MsgImageExtractor

Windows system tray utility that monitors folders for Outlook `.msg` files and extracts all image attachments automatically.

## Installation

```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Run the output `MsgImageExtractor.exe`. No .NET runtime or Outlook installation required.

## Usage

| Action | Description |
|---|---|
| Launch | App starts in the system tray; first launch opens Settings |
| Set watched folder | Choose the folder to monitor for incoming `.msg` files |
| Extract From Folder... | One-shot batch extraction from any folder |
| Pause / Resume | Temporarily stop monitoring |
| Settings | Configure output strategy, concurrency, filters |
| Exit | Stop the app |

```
MsgImageExtractor.exe
```

On first run, set your watched folder in Settings. Images are extracted alongside `.msg` files (SameFolder) or into a custom output directory.

## License

MIT
