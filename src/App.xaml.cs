// TODO: implement App.xaml.cs startup/shutdown orchestration
// Startup sequence:
//   1. Single-instance mutex check ("Global\MsgImageExtractorSingleInstance")
//   2. Directory initialisation (%APPDATA%\MsgImageExtractor\, logs\, processed\)
//   3. Settings load (defaults + clamp on corrupt/missing)
//   4. Sentinel pruning (foreground; watcher does not start until complete)
//   5. First-run check (open SettingsWindow if WatchedFolder is empty)
//   6. Queue and watcher start
//   7. Tray icon shown
// Shutdown sequence: disable watcher → cancel _shutdownCts → drain in-flight workers → flush logger

namespace MsgImageExtractor;
