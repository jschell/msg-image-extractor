using MsgImageExtractor.UI;
using System.Windows.Forms;
using Xunit;

namespace MsgImageExtractor.Tests.UI;

/// <summary>
/// NotificationManager wraps WinForms NotifyIcon.ShowBalloonTip which requires
/// a real OS handle — smoke tests only in this environment.
/// </summary>
public sealed class NotificationManagerTests
{
    [Fact]
    public void Constructor_WithNullIcon_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new NotificationManager(null!));
    }
}
