using RouterBalancing.Core.Notifications;

namespace router_balancing_test.Notifications;

/// <summary>
/// Test <see cref="NotificationChannelPolicy.Resolve"/> — decision table thuần,
/// không I/O: interactive (app đang nổi) → toast trong app; nền → notification
/// desktop; tắt setting → không kênh nào.
/// </summary>
public class NotificationChannelPolicyTests
{
    [Fact]
    public void Resolve_InteractiveAndEnabled_ReturnsInAppToast()
        => Assert.Equal(NotificationChannel.InAppToast, NotificationChannelPolicy.Resolve(true, true));

    [Fact]
    public void Resolve_NotInteractiveAndEnabled_ReturnsDesktopNotification()
        => Assert.Equal(NotificationChannel.DesktopNotification, NotificationChannelPolicy.Resolve(false, true));

    [Fact]
    public void Resolve_Disabled_ReturnsNone()
    {
        Assert.Equal(NotificationChannel.None, NotificationChannelPolicy.Resolve(true, false));
        Assert.Equal(NotificationChannel.None, NotificationChannelPolicy.Resolve(false, false));
    }
}
