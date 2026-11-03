using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Notifications;
using RouterBalancing.Core.Platform;
using RouterBalancing.Core.Settings;

namespace router_balancing.Services;

/// <summary>
/// Thực thi kênh thông báo cho từng <see cref="ErrorAlert"/> mà <see cref="ErrorAlertAggregator"/>
/// bắn ra (spec error-notify-client-name §2.3): toast ngay trong app khi cửa sổ đang mở + focus,
/// balloon tip của khay khi app chạy nền, và không gì khi user tắt setting notifyErrors.
/// <para>
/// Singleton và tự subscribe <see cref="ErrorAlertAggregator.AlertRaised"/> trong ctor để alert
/// nổ trước khi circuit Blazor đầu tiên render vẫn tới được khay. Vì thế nó **không** được
/// inject <c>ToastService</c> (scoped — bẫy DI của MAUI Blazor Hybrid): kênh toast đi qua
/// <see cref="InAppAlertRequested"/> và <c>MainLayout</c> trong Blazor scope mới gọi
/// <c>Toast.Show</c> (cùng pattern với <c>UpdateCheckService</c>).
/// </para>
/// </summary>
public sealed class ErrorNotifier : IDisposable
{
    private readonly ErrorAlertAggregator _alerts;
    private readonly IDesktopState _desktop;
    private readonly ITrayService _tray;
    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;

    /// <summary>
    /// Nối vào ctor của <see cref="ErrorAlertAggregator"/>; chính app (App.xaml.cs) là nơi tạo
    /// instance này qua DI để subscription được gắn từ sớm nhất.
    /// </summary>
    /// <param name="alerts">Nguồn alert đã gom cụm 30s.</param>
    /// <param name="desktop">Cờ mở/focus cửa sổ do app đặt — quyết định toast hay balloon.</param>
    /// <param name="tray">Kênh thông báo desktop; no-op trên nền tảng không có khay thật.</param>
    /// <param name="settings">Đọc setting notifyErrors mỗi lần bắn (đổi là có hiệu lực ngay).</param>
    /// <param name="log">Nơi ghi lỗi của chính đường thông báo (mức Warning — xem OnAlertRaised).</param>
    public ErrorNotifier(
        ErrorAlertAggregator alerts,
        IDesktopState desktop,
        ITrayService tray,
        IAppSettingsService settings,
        ILogService log)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _alerts = alerts;
        _desktop = desktop;
        _tray = tray;
        _settings = settings;
        _log = log;

        _alerts.AlertRaised += OnAlertRaised;
    }

    /// <summary>
    /// Alert cần hiển thị bằng toast trong app — <c>MainLayout</c> subscribe và gọi
    /// <c>Toast.Show</c> trong Blazor scope (spec §2.3). Không có subscriber → alert rơi về
    /// kênh desktop (xem <see cref="OnAlertRaised"/>).
    /// </summary>
    public event Action<ErrorAlert>? InAppAlertRequested;

    /// <summary>
    /// Chọn kênh theo <see cref="NotificationChannelPolicy"/> rồi thực thi.
    /// </summary>
    /// <remarks>
    /// Toàn bộ thân handler nằm trong một try/catch: event tới từ loop flush (worker thread) hoặc
    /// từ thread request (Flush đồng bộ), nên exception thoát ra đây sẽ giết loop flush hoặc làm
    /// hỏng request đang chạy.
    /// Warn chứ KHÔNG Error: dòng <c>Category=App, Severity=Error</c> lọt qua bộ lọc nhiễu
    /// §2.2.1 → chính việc thông báo hỏng sẽ nuôi một alert mới mỗi 30 giây, vô hạn
    /// (ErrorAlertAggregator.SafeWarn cũng cùng lý do).
    /// </remarks>
    private void OnAlertRaised(ErrorAlert alert)
    {
        try
        {
            switch (NotificationChannelPolicy.Resolve(_desktop.IsInteractive, _settings.NotifyErrors))
            {
                case NotificationChannel.InAppToast:
                    // Đọc delegate một lần: null-check và quyết định fallback dựa trên cùng một
                    // snapshot cho dễ đọc (?-Invoke vốn chỉ đọc field một lần, không phải race fix).
                    var toastHandlers = InAppAlertRequested;
                    // Không có subscriber = circuit Blazor chưa render (lỗi ngay lúc khởi động) hoặc
                    // đã dispose trong lúc cửa sổ vẫn focus → alert sẽ rơi im lặng, đúng kịch bản
                    // motivating của tính năng. Rơi về kênh desktop (dùng lại đường dispatch sẵn có)
                    // để không bao giờ mất thông báo (spec §2.3).
                    if (toastHandlers is null)
                    {
                        ShowDesktopNotification(alert);
                    }
                    else
                    {
                        toastHandlers(alert);
                    }
                    break;

                case NotificationChannel.DesktopNotification:
                    ShowDesktopNotification(alert);
                    break;

                default:
                    // None — user đã tắt setting. Aggregator vẫn chạy (không unsubscribe) để bật
                    // setting lại là thông báo hoạt động mà không cần reconnect (spec §2.5).
                    break;
            }
        }
        catch (Exception ex)
        {
            // Warn chứ không Error: xem giải thích ở XML doc của OnAlertRaised. ILogService.Warn
            // không có overload nhận Exception (chỉ Error mới có) nên đưa loại + thông điệp ngoại
            // lệ vào message — cùng pattern với ErrorAlertAggregator.SafeWarn (Task 7).
            _log.Warn($"Không gửi được thông báo lỗi: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Balloon tip bắt buộc chạy trên UI thread đã tạo <c>NotifyIcon</c>, còn <c>AlertRaised</c>
    /// tới từ worker/flush loop → luôn đi qua <c>Dispatcher.Dispatch</c>.
    /// </summary>
    private void ShowDesktopNotification(ErrorAlert alert)
    {
        var app = Application.Current;
        if (app is null)
        {
            // Alert nổ trước khi MAUI Application tồn tại (hiếm): TrayService đã tự guard khi
            // icon chưa Initialize nên gọi thẳng là an toàn — không có Dispatcher để bọc.
            _tray.Notify(alert.Title, alert.Message);
            return;
        }

        app.Dispatcher.Dispatch(() => NotifyOnUIThread(alert));
    }

    /// <summary>
    /// Delegate đã được dispatch chạy ngoài try/catch của <see cref="OnAlertRaised"/> — phải tự
    /// cách ly, nếu không một balloon tip hỏng sẽ tới AppDomain.UnhandledException.
    /// </summary>
    private void NotifyOnUIThread(ErrorAlert alert)
    {
        try
        {
            _tray.Notify(alert.Title, alert.Message);
        }
        catch (Exception ex)
        {
            // Cùng luật Warn ở OnAlertRaised: Error sẽ tự nuôi alert mới.
            _log.Warn($"Không gửi được thông báo lỗi ra desktop (UI thread): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Cắt subscription khỏi aggregator (aggregator là singleton sống suốt đời app).
    /// Idempotent — <c>-=</c> với handler chưa có là no-op.
    /// </summary>
    public void Dispose() => _alerts.AlertRaised -= OnAlertRaised;
}
