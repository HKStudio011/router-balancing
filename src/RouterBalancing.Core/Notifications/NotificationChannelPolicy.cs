namespace RouterBalancing.Core.Notifications;

/// <summary>Kênh hiển thị thông báo lỗi cho người dùng.</summary>
public enum NotificationChannel
{
    /// <summary>Không thông báo (setting tắt).</summary>
    None,

    /// <summary>Toast ngay trong cửa sổ app (app đang tương tác).</summary>
    InAppToast,

    /// <summary>Notification của hệ điều hành (Windows) — dùng khi app đang chạy nền.</summary>
    DesktopNotification,
}

/// <summary>
/// Quyết định kênh thông báo dựa trên trạng thái tương tác của app và setting
/// notifyErrors. Pure function — app layer mới là nơi thực thi kênh được chọn.
/// </summary>
public static class NotificationChannelPolicy
{
    /// <summary>
    /// Chọn kênh cho một <see cref="ErrorAlert"/>.
    /// </summary>
    /// <param name="isInteractive">true khi cửa sổ app đang mở và người dùng đang tương tác.</param>
    /// <param name="notificationsEnabled">giá trị setting <c>NotifyErrors</c>.</param>
    /// <returns>Kênh cần dùng; <see cref="NotificationChannel.None"/> khi người dùng đã tắt thông báo.</returns>
    public static NotificationChannel Resolve(bool isInteractive, bool notificationsEnabled)
        => notificationsEnabled
            ? isInteractive ? NotificationChannel.InAppToast : NotificationChannel.DesktopNotification
            : NotificationChannel.None;
}
