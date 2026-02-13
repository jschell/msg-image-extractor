// TODO: implement Settings
// Persists to %APPDATA%\MsgImageExtractor\settings.json
// Fields: WatchedFolder, OutputStrategy, CustomFolder, MaxConcurrentExtractions,
//         SkipExisting, IncludeSubdirectories, FileNamingPattern,
//         MinAttachmentSizeKb, ExtractInlineImages, RecurseEmbeddedMessages, DeduplicateByHash
// Flush to JSON on every write. Clamp out-of-range values on load.

namespace MsgImageExtractor.Core;

public enum OutputStrategy { SameFolder, CustomFolder }
