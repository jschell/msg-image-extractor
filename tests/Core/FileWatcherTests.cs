using MsgImageExtractor.Core;
using Xunit;

namespace MsgImageExtractor.Tests.Core;

public sealed class FileWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Logger _logger;

    public FileWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WatcherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logger = new Logger(Path.Combine(_tempDir, "logs"));
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Start_ValidPath_DoesNotThrow()
    {
        using var watcher = new FileWatcher(_logger);
        var ex = Record.Exception(() => watcher.Start(_tempDir, includeSubdirs: false));
        Assert.Null(ex);
    }

    [Fact]
    public void Start_InvalidPath_LogsErrorDoesNotThrow()
    {
        using var watcher = new FileWatcher(_logger);
        var ex = Record.Exception(() => watcher.Start(@"Z:\NonExistent\Path\999", includeSubdirs: false));
        Assert.Null(ex); // should not throw — logged as error
    }

    [Fact]
    public void Stop_WithoutStart_DoesNotThrow()
    {
        using var watcher = new FileWatcher(_logger);
        var ex = Record.Exception(() => watcher.Stop());
        Assert.Null(ex);
    }

    [Fact]
    public async Task FileDetected_FiredWhenMsgFileCreated()
    {
        using var watcher = new FileWatcher(_logger);
        var detected = new List<string>();
        watcher.FileDetected += p => detected.Add(p);
        watcher.Start(_tempDir, includeSubdirs: false);

        var path = Path.Combine(_tempDir, "test.msg");
        await File.WriteAllTextAsync(path, "content");

        await Task.Delay(500);
        watcher.Stop();

        Assert.Contains(path, detected);
    }

    [Fact]
    public async Task DedupWindow_SamePathWithin2s_FiresOnlyOnce()
    {
        using var watcher = new FileWatcher(_logger);
        var count = 0;
        watcher.FileDetected += _ => Interlocked.Increment(ref count);
        watcher.Start(_tempDir, includeSubdirs: false);

        // Simulate two rapid events for the same path via the internal OnFileEvent method
        var testPath = Path.Combine(_tempDir, "dup.msg");
        watcher.SimulateFileEvent(testPath);
        watcher.SimulateFileEvent(testPath); // within 2s — should be suppressed

        await Task.Delay(100);
        watcher.Stop();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DedupWindow_SamePathAfter2s_FiresTwice()
    {
        using var watcher = new FileWatcher(_logger);
        var count = 0;
        watcher.FileDetected += _ => Interlocked.Increment(ref count);
        watcher.Start(_tempDir, includeSubdirs: false);

        var testPath = Path.Combine(_tempDir, "dup2.msg");
        watcher.SimulateFileEvent(testPath);
        await Task.Delay(2100); // past 2-second window
        watcher.SimulateFileEvent(testPath);

        await Task.Delay(100);
        watcher.Stop();

        Assert.Equal(2, count);
    }
}
