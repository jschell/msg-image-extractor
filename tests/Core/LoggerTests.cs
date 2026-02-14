using MsgImageExtractor.Core;
using System.IO;
using Xunit;

namespace MsgImageExtractor.Tests.Core;

public sealed class LoggerTests : IDisposable
{
    private readonly string _tempDir;

    public LoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LoggerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void WritesInfo_CreatesLogFile()
    {
        using var logger = new Logger(_tempDir);
        logger.Info("hello info");
        var files = Directory.GetFiles(_tempDir, "*.log");
        Assert.Single(files);
    }

    [Fact]
    public void WritesInfo_ContainsInfoLevel()
    {
        using var logger = new Logger(_tempDir);
        logger.Info("test info message");
        var content = File.ReadAllText(Directory.GetFiles(_tempDir, "*.log")[0]);
        Assert.Contains("[INFO]", content);
        Assert.Contains("test info message", content);
    }

    [Fact]
    public void WritesWarning_ContainsWarnLevel()
    {
        using var logger = new Logger(_tempDir);
        logger.Warning("test warning");
        var content = File.ReadAllText(Directory.GetFiles(_tempDir, "*.log")[0]);
        Assert.Contains("[WARN]", content);
        Assert.Contains("test warning", content);
    }

    [Fact]
    public void WritesError_ContainsErrorLevel()
    {
        using var logger = new Logger(_tempDir);
        logger.Error("test error");
        var content = File.ReadAllText(Directory.GetFiles(_tempDir, "*.log")[0]);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("test error", content);
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        using var logger = new Logger(_tempDir);
        logger.Info("before flush");
        var ex = Record.Exception(() => logger.Flush());
        Assert.Null(ex);
    }

    [Fact]
    public void LogFilename_ContainsDate()
    {
        using var logger = new Logger(_tempDir);
        logger.Info("any");
        var filename = Path.GetFileName(Directory.GetFiles(_tempDir, "*.log")[0]);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Contains(today, filename);
        Assert.StartsWith("msgextractor-", filename);
    }

    [Fact]
    public void MultipleWrites_AllAppendedToSameFile()
    {
        using var logger = new Logger(_tempDir);
        logger.Info("line1");
        logger.Warning("line2");
        logger.Error("line3");
        var files = Directory.GetFiles(_tempDir, "*.log");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(3, lines.Length);
    }
}
