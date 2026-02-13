using MsgImageExtractor.UI;
using Xunit;

namespace MsgImageExtractor.Tests.UI;

public sealed class SystemTrayIconTests
{
    // -------------------------------------------------------------------------
    // Status string builder tests (pure logic, no WinForms handles needed)
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStatus_AllZero_ReturnsMonitoringOnly()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false,
            since: since,
            extracted: 0,
            failed: 0,
            skipped: 0,
            dropped: 0,
            pendingRetry: 0);

        Assert.Equal("Monitoring since 09:32", status);
    }

    [Fact]
    public void BuildStatus_ExtractedOnly_ShowsExtracted()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false, since: since,
            extracted: 14, failed: 0, skipped: 0, dropped: 0, pendingRetry: 0);

        Assert.Equal("Monitoring since 09:32 — 14 extracted", status);
    }

    [Fact]
    public void BuildStatus_ExtractedAndSkipped_NoFailed()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false, since: since,
            extracted: 14, failed: 0, skipped: 2, dropped: 0, pendingRetry: 0);

        Assert.Equal("Monitoring since 09:32 — 14 extracted, 2 skipped", status);
    }

    [Fact]
    public void BuildStatus_AllCounters_ShowsAll()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false, since: since,
            extracted: 14, failed: 1, skipped: 2, dropped: 3, pendingRetry: 0);

        Assert.Equal("Monitoring since 09:32 — 14 extracted, 1 failed, 2 skipped, 3 dropped", status);
    }

    [Fact]
    public void BuildStatus_WithPendingRetry_AppendedLast()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false, since: since,
            extracted: 14, failed: 0, skipped: 2, dropped: 3, pendingRetry: 1);

        Assert.Equal("Monitoring since 09:32 — 14 extracted, 2 skipped, 3 dropped, 1 pending retry", status);
    }

    [Fact]
    public void BuildStatus_Paused_ShowsPausedSince()
    {
        var since = new DateTime(2024, 1, 15, 9, 45, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: true, since: since,
            extracted: 14, failed: 0, skipped: 2, dropped: 0, pendingRetry: 0);

        Assert.Contains("Paused since 09:45", status);
        Assert.Contains("14 extracted", status);
    }

    [Fact]
    public void BuildStatus_OrderIsFixed_ExtractedFailedSkippedDropped()
    {
        var since = new DateTime(2024, 1, 15, 9, 32, 0);
        var status = SystemTrayIcon.BuildStatusString(
            paused: false, since: since,
            extracted: 5, failed: 1, skipped: 2, dropped: 3, pendingRetry: 4);

        // Verify order
        var iExtracted = status.IndexOf("extracted", StringComparison.Ordinal);
        var iFailed = status.IndexOf("failed", StringComparison.Ordinal);
        var iSkipped = status.IndexOf("skipped", StringComparison.Ordinal);
        var iDropped = status.IndexOf("dropped", StringComparison.Ordinal);
        var iRetry = status.IndexOf("pending retry", StringComparison.Ordinal);

        Assert.True(iExtracted < iFailed);
        Assert.True(iFailed < iSkipped);
        Assert.True(iSkipped < iDropped);
        Assert.True(iDropped < iRetry);
    }

    [Fact]
    public void BuildStatus_NotConfigured_ReturnsNotConfiguredString()
    {
        var status = SystemTrayIcon.BuildNotConfiguredString();
        Assert.Equal("Not configured — click Settings to set up", status);
    }
}
