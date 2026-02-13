// TODO: implement Logger
// Plain file logger — no external dependency.
// File path: %APPDATA%\MsgImageExtractor\logs\msgextractor-{yyyy-MM-dd}.log
// Format: {HH:mm:ss.fff} [{LEVEL}] {message}
// Levels: Info, Warning, Error
// StreamWriter with AutoFlush = true, locked for thread safety.

namespace MsgImageExtractor.Core;
