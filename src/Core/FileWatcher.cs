// TODO: implement FileWatcher
// Watches *.msg and *.MSG via FileSystemWatcher.
// Subscribes to Created and Renamed events.
// 2-second deduplication window via ConcurrentDictionary<string, DateTime>.
// Auto-restart on watcher error with up to 3 retries.

namespace MsgImageExtractor.Core;
