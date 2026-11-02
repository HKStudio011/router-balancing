namespace RouterBalancing.Core.Platform;

/// <summary>Biểu tượng khay hệ thống — bấm đúp/mở → hiện cửa sổ, Exit → thoát app.</summary>
public interface ITrayService : IDisposable
{
    /// <summary>Yêu cầu hiện lại cửa sổ chính.</summary>
    event Action? OpenRequested;

    /// <summary>Yêu cầu thoát app hoàn toàn (khác với đóng cửa sổ về khay).</summary>
    event Action? ExitRequested;

    /// <summary>Tạo icon trên khay — gọi sau khi DI sẵn sàng; idempotent.</summary>
    void Initialize();

    /// <summary>
    /// Gửi một thông báo desktop (Windows: balloon tip của NotifyIcon).
    /// </summary>
    /// <remarks>
    /// Dùng balloon tip thay toast WinUI (<c>ToastNotificationManager</c>): app chạy unpackaged
    /// nên toast WinUI phải đăng ký AUMID + shortcut Start Menu mới hiện được, còn balloon tip
    /// tự vào Action Center trên Win10/11 mà không thêm dependency nào.
    /// Nền tảng không có khay thật (Android/iOS/mac) là no-op.
    /// </remarks>
    /// <param name="title">Tiêu đề thông báo.</param>
    /// <param name="message">Nội dung — đã được cap độ dài ở tầng aggregator (spec §2.2)
    /// vì Windows cắt text balloon tip quá dài.</param>
    void Notify(string title, string message);

    /// <summary>
    /// User bấm vào thông báo desktop — app mở cửa sổ và điều hướng tới Log panel (spec §2.4).
    /// Windows: chỉ bắn từ <c>NotifyIcon.BalloonTipClicked</c> khi balloon tip CÒN hiển thị trên
    /// màn hình (event tới từ NIN_BALLOONUSERCLICK); bấm mục đã nằm trong Action Center KHÔNG bắn
    /// event này — khả năng click-to-open chỉ có trong thời gian balloon còn trên màn hình.
    /// </summary>
    event Action? NotificationActivated;
}
