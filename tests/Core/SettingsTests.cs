using MsgImageExtractor.Core;
using System.Text.Json;
using Xunit;

namespace MsgImageExtractor.Tests.Core;

public sealed class SettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SettingsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var s = new Settings();
        Assert.Equal(string.Empty, s.WatchedFolder);
        Assert.Equal(OutputStrategy.SameFolder, s.OutputStrategy);
        Assert.Equal(string.Empty, s.CustomFolder);
        Assert.Equal(2, s.MaxConcurrentExtractions);
        Assert.True(s.SkipExisting);
        Assert.True(s.IncludeSubdirectories);
        Assert.Equal("{msgname}_{attachment}", s.FileNamingPattern);
        Assert.Equal(10, s.MinAttachmentSizeKb);
        Assert.False(s.ExtractInlineImages);
        Assert.False(s.RecurseEmbeddedMessages);
        Assert.True(s.DeduplicateByHash);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(10, s.MinAttachmentSizeKb);
        Assert.Equal(2, s.MaxConcurrentExtractions);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ not valid json !!!}}}");
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(10, s.MinAttachmentSizeKb);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var s = new Settings { WatchedFolder = @"C:\Watch", MinAttachmentSizeKb = 50 };
        s.SaveTo(_settingsPath);
        var loaded = Settings.LoadFrom(_settingsPath);
        Assert.Equal(@"C:\Watch", loaded.WatchedFolder);
        Assert.Equal(50, loaded.MinAttachmentSizeKb);
    }

    [Fact]
    public void Clamp_MinAttachmentSizeKb_BelowMin_ClampsTo1()
    {
        File.WriteAllText(_settingsPath,
            JsonSerializer.Serialize(new { MinAttachmentSizeKb = 0 }));
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(1, s.MinAttachmentSizeKb);
    }

    [Fact]
    public void Clamp_MinAttachmentSizeKb_AboveMax_ClampsTo10000()
    {
        File.WriteAllText(_settingsPath,
            JsonSerializer.Serialize(new { MinAttachmentSizeKb = 99999 }));
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(10_000, s.MinAttachmentSizeKb);
    }

    [Fact]
    public void Clamp_MaxConcurrentExtractions_BelowMin_ClampsTo1()
    {
        File.WriteAllText(_settingsPath,
            JsonSerializer.Serialize(new { MaxConcurrentExtractions = 0 }));
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(1, s.MaxConcurrentExtractions);
    }

    [Fact]
    public void Clamp_MaxConcurrentExtractions_AboveMax_ClampsTo8()
    {
        File.WriteAllText(_settingsPath,
            JsonSerializer.Serialize(new { MaxConcurrentExtractions = 100 }));
        var s = Settings.LoadFrom(_settingsPath);
        Assert.Equal(8, s.MaxConcurrentExtractions);
    }

    [Fact]
    public void OutputStrategy_SameFolder_RoundTrips()
    {
        var s = new Settings { OutputStrategy = OutputStrategy.SameFolder };
        s.SaveTo(_settingsPath);
        var loaded = Settings.LoadFrom(_settingsPath);
        Assert.Equal(OutputStrategy.SameFolder, loaded.OutputStrategy);
    }

    [Fact]
    public void OutputStrategy_CustomFolder_RoundTrips()
    {
        var s = new Settings { OutputStrategy = OutputStrategy.CustomFolder, CustomFolder = @"C:\Out" };
        s.SaveTo(_settingsPath);
        var loaded = Settings.LoadFrom(_settingsPath);
        Assert.Equal(OutputStrategy.CustomFolder, loaded.OutputStrategy);
        Assert.Equal(@"C:\Out", loaded.CustomFolder);
    }
}
