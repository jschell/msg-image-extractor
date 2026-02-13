// TODO: implement ExtractionQueue
// Channel-based worker pool (bounded capacity 1024, DropWrite).
// Configurable MaxConcurrentExtractions (default 2).
// Exposes BlockWorkers/UnblockWorkers, ExtractionCompleted event, QueueCountChanged event.
// Owns session counters: ExtractedCount, FailedCount, SkippedCount, DroppedCount.

namespace MsgImageExtractor.Core;
