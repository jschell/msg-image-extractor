using MsgImageExtractor.Core;
using System.Collections.Concurrent;
using System.IO;
using Xunit;

namespace MsgImageExtractor.Tests.Core;

public sealed class ExtractionEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Settings _settings;

    public ExtractionEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"EngineTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new Settings
        {
            OutputStrategy = OutputStrategy.CustomFolder,
            CustomFolder = _tempDir,
            SkipExisting = false
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -------------------------------------------------------------------------
    // SanitiseName
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("file:name", "file_name")]
    [InlineData("file/name", "file_name")]
    [InlineData("file\\name", "file_name")]
    [InlineData("file*name", "file_name")]
    [InlineData("file?name", "file_name")]
    [InlineData("file\"name", "file_name")]
    [InlineData("file<name>", "file_name_")]
    [InlineData("file|name", "file_name")]
    [InlineData("normal", "normal")]
    public void SanitiseName_ReplacesIllegalChars(string input, string expected)
    {
        var result = ExtractionEngine.SanitiseName(input);
        Assert.Equal(expected, result);
    }

    // -------------------------------------------------------------------------
    // BuildOutputFilename
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildOutputFilename_DefaultPattern_ProducesExpectedName()
    {
        var filename = ExtractionEngine.BuildOutputFilename(
            msgName: "Invoice 2024-03",
            attachmentName: "photo.jpg",
            pattern: "{msgname}_{attachment}");
        Assert.Equal("Invoice 2024-03_photo.jpg", filename);
    }

    [Fact]
    public void BuildOutputFilename_ReplacesIllegalCharsInMsgName()
    {
        var filename = ExtractionEngine.BuildOutputFilename(
            msgName: "Re: Invoice",
            attachmentName: "photo.jpg",
            pattern: "{msgname}_{attachment}");
        Assert.Equal("Re_ Invoice_photo.jpg", filename);
    }

    // -------------------------------------------------------------------------
    // ResolveCollision
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveCollision_NoConflict_ReturnsSamePath()
    {
        var path = Path.Combine(_tempDir, "invoice_photo.jpg");
        // File does not exist — no collision
        var result = ExtractionEngine.ResolveCollision(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolveCollision_OneConflict_AddsCounterSuffix_2()
    {
        var path = Path.Combine(_tempDir, "invoice_photo.jpg");
        File.WriteAllText(path, "existing");
        var result = ExtractionEngine.ResolveCollision(path);
        Assert.Equal(Path.Combine(_tempDir, "invoice_photo_2.jpg"), result);
    }

    [Fact]
    public void ResolveCollision_TwoConflicts_AddsCounterSuffix_3()
    {
        var path = Path.Combine(_tempDir, "invoice_photo.jpg");
        File.WriteAllText(path, "existing");
        File.WriteAllText(Path.Combine(_tempDir, "invoice_photo_2.jpg"), "existing2");
        var result = ExtractionEngine.ResolveCollision(path);
        Assert.Equal(Path.Combine(_tempDir, "invoice_photo_3.jpg"), result);
    }

    [Fact]
    public void ResolveCollision_CounterInsertedBeforeExtension()
    {
        var path = Path.Combine(_tempDir, "file.tar.gz");
        File.WriteAllText(path, "x");
        var result = ExtractionEngine.ResolveCollision(path);
        // Last dot is the extension separator
        Assert.Equal(Path.Combine(_tempDir, "file.tar_2.gz"), result);
    }

    // -------------------------------------------------------------------------
    // GetSentinelPath
    // -------------------------------------------------------------------------

    [Fact]
    public void GetSentinelPath_DifferentPaths_ProduceDifferentSentinels()
    {
        var s1 = ExtractionEngine.GetSentinelPath(@"C:\folder\a.msg", _tempDir);
        var s2 = ExtractionEngine.GetSentinelPath(@"C:\folder\b.msg", _tempDir);
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void GetSentinelPath_SamePath_ProducesSameSentinel()
    {
        var s1 = ExtractionEngine.GetSentinelPath(@"C:\folder\a.msg", _tempDir);
        var s2 = ExtractionEngine.GetSentinelPath(@"C:\folder\a.msg", _tempDir);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void GetSentinelPath_HasDotSentinelExtension()
    {
        var s = ExtractionEngine.GetSentinelPath(@"C:\x.msg", _tempDir);
        Assert.EndsWith(".sentinel", s);
    }

    // -------------------------------------------------------------------------
    // ExtractAsync — AlreadyProcessed (SkipExisting)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_AlreadyProcessed_ReturnsAlreadyProcessedResult()
    {
        var processedDir = Path.Combine(_tempDir, "processed");
        Directory.CreateDirectory(processedDir);
        var settings = new Settings
        {
            OutputStrategy = OutputStrategy.CustomFolder,
            CustomFolder = _tempDir,
            SkipExisting = true
        };

        // Create a fake .msg file path and write its sentinel
        var fakeMsgPath = Path.Combine(_tempDir, "test.msg");
        File.WriteAllText(fakeMsgPath, "not a real msg");
        var sentinelPath = ExtractionEngine.GetSentinelPath(fakeMsgPath, processedDir);
        File.WriteAllText(sentinelPath, fakeMsgPath);

        var engine = new ExtractionEngine(processedDir, new Logger(Path.Combine(_tempDir, "logs")));
        var hashSet = new ConcurrentDictionary<string, byte>();
        var result = await engine.ExtractAsync(fakeMsgPath, settings, hashSet, CancellationToken.None);

        Assert.True(result.AlreadyProcessed);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExtractedCount);
    }
}
