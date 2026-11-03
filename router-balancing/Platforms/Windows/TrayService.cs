using System.Drawing;
using System.Windows.Forms;
using RouterBalancing.Core.Localization;
using RouterBalancing.Core.Platform;

namespace router_balancing.Platforms.Windows;

/// <summary>
/// Icon khay bằng WinForms NotifyIcon — chạy trên UI thread WinUI
/// (đã có message pump nên không cần Application.Run).
/// </summary>
internal sealed class TrayService : ITrayService
{
    private readonly LocalizationService _localization;
    private NotifyIcon? _icon;
    private ToolStripMenuItem _openItem = null!;
    private ToolStripMenuItem _exitItem = null!;

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public event Action? NotificationActivated;

    public TrayService(LocalizationService localization) => _localization = localization;

    public void Initialize()
    {
        if (_icon is not null) return;

        _openItem = new ToolStripMenuItem(_localization["tray.open"], null, (_, _) => OpenRequested?.Invoke());
        _exitItem = new ToolStripMenuItem(_localization["tray.exit"], null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            // Icon app (appicon.ico cạnh exe, copy theo Content trong csproj) —
            // fallback icon hệ thống khi file thiếu (build lỗi copy, user xóa tay).
            Icon = LoadAppIcon() ?? SystemIcons.Application,
            Text = _localization["app.title"],
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        // Bấm balloon tip lúc nó CÒN hiển thị → mở app tới Log panel (§2.4). Event tới từ
        // NIN_BALLOONUSERCLICK: mục đã vào Action Center không bắn event — click-to-open
        // chỉ khả dụng trong thời gian balloon còn trên màn hình
        _icon.BalloonTipClicked += (_, _) => NotificationActivated?.Invoke();

        _localization.LanguageChanged += UpdateMenuTexts;
    }

    /// <summary>
    /// Gửi balloon tip trên khay (icon Error). Thời lượng xin 6s chỉ là yêu cầu — Shell cắt
    /// timeout theo ý nó và chỉ hiển thị một balloon tại một thời điểm.
    /// </summary>
    /// <remarks>
    /// Dùng balloon tip thay toast WinUI (ToastNotificationManager): app unpackaged nên toast
    /// WinUI phải đăng ký AUMID + shortcut Start Menu mới hiện được; balloon tip của NotifyIcon
    /// hiện vào Action Center trên Win10/11 mà không thêm dependency nào (spec §2.3).
    /// Phải gọi trên UI thread đã tạo NotifyIcon — caller (ErrorNotifier) bọc trong
    /// Dispatcher.Dispatch vì AlertRaised tới từ worker/flush loop.
    /// </remarks>
    public void Notify(string title, string message)
    {
        // Tray chưa Initialize (hoặc đã Dispose sau Exit) — không có gì để bắn, return sớm
        if (_icon is null) return;
        // ShowBalloonTip ném ArgumentException khi tipText rỗng — ErrorAlertMessage.Build (count==1)
        // trả message của LogEntry rỗng nguyên văn, nên dòng Error có Message trống sẽ làm
        // throw ngay trong đường alert và mất luôn thông báo → bỏ qua balloon, không ném
        if (string.IsNullOrWhiteSpace(message)) return;
        _icon.ShowBalloonTip(6000, title, message, ToolTipIcon.Error);
    }

    /// <summary>Đọc appicon.ico cạnh exe; null khi file thiếu/hỏng (caller fallback SystemIcons).</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException)
        {
            // File hỏng (icon không hợp lệ) hoặc không đọc được — không chặn app khởi động
            return null;
        }
    }

    private void UpdateMenuTexts()
    {
        _openItem.Text = _localization["tray.open"];
        _exitItem.Text = _localization["tray.exit"];
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _localization.LanguageChanged -= UpdateMenuTexts;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
