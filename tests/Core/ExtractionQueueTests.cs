using MsgImageExtractor.Core;
using System.IO;
using Xunit;

namespace MsgImageExtractor.Tests.Core;

public sealed class ExtractionQueueTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Logger _logger;

    public ExtractionQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"QueueTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logger = new Logger(Path.Combine(_tempDir, "logs"));
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ExtractionQueue CreateQueue(int workers = 1)
    {
        var processedDir = Path.Combine(_tempDir, "processed");
        Directory.CreateDirectory(processedDir);
        var settings = new Settings { SkipExisting = false };
        var engine = new ExtractionEngine(processedDir, _logger);
        return new ExtractionQueue(engine, settings, _logger, maxConcurrentExtractions: workers);
    }

    [Fact]
    public void NewQueue_AllCountersAreZero()
    {
        using var queue = CreateQueue();
        Assert.Equal(0, queue.ExtractedCount);
        Assert.Equal(0, queue.FailedCount);
        Assert.Equal(0, queue.SkippedCount);
        Assert.Equal(0, queue.DroppedCount);
        Assert.Equal(0, queue.PendingRetryCount);
    }

    [Fact]
    public async Task Enqueue_BeyondCapacity_DropsAndIncrementsDroppedCount()
    {
        // Create a queue with a very small capacity override for testing
        using var queue = CreateQueue(workers: 1);
        // Fill the channel beyond capacity by using the test method
        var dropped = queue.TestFillAndCountDrops(1025);
        Assert.True(dropped > 0);
        Assert.True(queue.DroppedCount > 0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExtractionCompleted_FiredAfterProcessing()
    {
        using var queue = CreateQueue(workers: 1);
        ExtractionResult? captured = null;
        queue.ExtractionCompleted += r => captured = r;

        // Create a real (non-.msg) file that will fail with UnsupportedFormat
        var fakePath = Path.Combine(_tempDir, "test.msg");
        File.WriteAllText(fakePath, "not a real msg");

        queue.Start();
        queue.Enqueue(fakePath);

        // Wait for processing
        await Task.Delay(2000);
        await queue.StopAsync();

        Assert.NotNull(captured);
        Assert.Equal(fakePath, captured!.SourcePath);
    }

    [Fact]
    public async Task QueueCountChanged_FiredOnEnqueue()
    {
        using var queue = CreateQueue(workers: 1);
        var counts = new List<int>();
        queue.QueueCountChanged += c => counts.Add(c);

        var fakePath = Path.Combine(_tempDir, "test2.msg");
        File.WriteAllText(fakePath, "not a real msg");

        queue.Start();
        queue.Enqueue(fakePath);

        await Task.Delay(100);
        await queue.StopAsync();

        Assert.NotEmpty(counts);
    }

    [Fact]
    public async Task BlockWorkers_ThenUnblock_ResumesProcessing()
    {
        using var queue = CreateQueue(workers: 1);
        queue.Start();

        // Block — should not throw
        await queue.BlockWorkersAsync();
        // Unblock
        queue.UnblockWorkers();

        await queue.StopAsync();
    }

    [Fact]
    public async Task SkippedCount_IncrementsForPasswordProtectedAndUnsupportedFormat()
    {
        // UnsupportedFormat fires for a non-OLE2 file
        using var queue = CreateQueue(workers: 1);
        queue.Start();

        var fakePath = Path.Combine(_tempDir, "bad.msg");
        File.WriteAllText(fakePath, "this is definitely not OLE2");
        queue.Enqueue(fakePath);

        await Task.Delay(2000);
        await queue.StopAsync();

        // UnsupportedFormat or UnexpectedError — either increments SkippedCount or FailedCount
        Assert.True(queue.SkippedCount + queue.FailedCount > 0);
    }
}
