using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Notifications;
using RouterBalancing.Core.Platform;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Update;
using router_balancing.Services;
// Không import Microsoft.UI.Xaml: Application/Window trùng tên Microsoft.Maui.Controls (global using) → CS0104
#if WINDOWS
using Microsoft.UI.Windowing;
#endif

namespace router_balancing
{
    public partial class App : Application
    {
        private readonly ILogService _log;
        private readonly IProxyHost _proxyHost;
        private readonly IAppSettingsService _settings;
        private readonly IStartupRegistration _startup;
        private readonly ITrayService _tray;
        private readonly LogRetentionWorker _retention;
        private readonly DatasetSink _dataset;
        private readonly FreeModelSyncWorker _freeSync;
        private readonly UpdateCheckService _updateCheck;
        private readonly WindowState _window;
        private readonly ErrorAlertAggregator _alerts;
        private readonly ErrorNotifier _notifier;
        private readonly AppNavigation _nav;
        private Window? _mainWindow;
#if WINDOWS
        // Chỉ Windows dùng: phân biệt thoát từ tray với đóng cửa sổ về khay (AppWindow.Closing)
        private bool _reallyExiting;
#endif

        public App(
            ILogService log,
            IProxyHost proxyHost,
            IAppSettingsService settings,
            IStartupRegistration startup,
            ITrayService tray,
            LogRetentionWorker retention,
            DatasetSink dataset,
            FreeModelSyncWorker freeSync,
            UpdateCheckService updateCheck,
            WindowState window,
            ErrorAlertAggregator alerts,
            ErrorNotifier notifier,
            AppNavigation nav)
        {
            InitializeComponent();
            _log = log;
            _proxyHost = proxyHost;
            _settings = settings;
            _startup = startup;
            _tray = tray;
            _retention = retention;
            _dataset = dataset;
            _freeSync = freeSync;
            _updateCheck = updateCheck;
            _window = window;
            _alerts = alerts;
            // ctor của ErrorNotifier là nơi subscribe AlertRaised — resolve qua DI để nó gắn
            // subscription trước khi circuit Blazor đầu tiên tồn tại; giữ field để Dispose ở
            // StopProxyOnExit cắt subscription đó khi process thoát.
            _notifier = notifier;
            _nav = nav;

            _log.Info("router-balancing khởi động.");

            // Chặn crash âm thầm: exception ngoài luồng render (event handler Blazor Hybrid,
            // background task) không có ErrorBoundary nào bắt — ghi vào DB trước khi process chết
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try
                {
                    _log.Error("Unhandled exception (AppDomain).", args.ExceptionObject as Exception);
                }
                catch
                {
                    // Last resort: không được ném tiếp từ handler này
                }
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                try
                {
                    _log.Error("Unobserved task exception.", args.Exception);
                    args.SetObserved();
                }
                catch
                {
                }
            };

            // Dọn log quá hạn ngay khi mở app rồi lặp 24h — retention không cần user bấm
            _retention.Start();

            // Sync model free: trễ 60s rồi lặp 6h — preset free cần list model mới mà user không tự bấm
            _freeSync.Start();

            // Check update nền: trễ 5s rồi lặp 24h — phát hiện bản mới thì MainLayout toast (spec §9.4)
            _updateCheck.Start();

            // Gom cụm lỗi 30s (loop flush 1s) — đặt ngoài #if WINDOWS: mọi nền tảng đều gom, chỉ
            // kênh thông báo là khác nhau (nền tảng không có tray thật thì im lặng, spec §2.3)
            _alerts.Start();

            _tray.OpenRequested += ShowMainWindow;
            _tray.ExitRequested += ExitFromTray;
            // Bấm balloon tip lúc nó CÒN hiển thị → mở cửa sổ rồi tới Log panel đã lọc lỗi (§2.4).
            // Phần navigation phải rời handler WinForms trước — xem remarks của OnNotificationActivated.
            _tray.NotificationActivated += OnNotificationActivated;
            _tray.Initialize();

            SyncStartupRegistration();
            // Ngắt kết nối socket Kestrel khi process thoát — kể cả khi người dùng tắt từ Task Manager
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopProxyOnExit();

            // Start nền để UI render không bị Kestrel block; lỗi chỉ log, không crash app
            _ = Task.Run(async () =>
            {
                try
                {
                    await _proxyHost.StartAsync();
                }
                catch (Exception ex)
                {
                    _log.Error("Không khởi động được proxy server.", ex);
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = "router-balancing" };
            _mainWindow = window;
#if WINDOWS
            AttachCloseToTray(window);
#endif
            return window;
        }

        /// <summary>Setting là nguồn sự thật — đồng bộ registry khi app khởi động (user có thể sửa tay ngoài app).</summary>
        private void SyncStartupRegistration()
        {
            try
            {
                // true ⇒ Run key phải trỏ đúng exe hiện tại: IsEnabled=false khi thiếu HOẶC path cũ
                // (exe bị di chuyển) → ghi lại để sửa giá trị lỗi thời.
                // false ⇒ xóa key thẳng, không tra IsEnabled: path lỗi thời vẫn phải bị xóa
                // (DeleteValue idempotent — không lỗi khi key chưa tồn tại).
                if (!_settings.StartWithWindows || !_startup.IsEnabled)
                {
                    _startup.SetEnabled(_settings.StartWithWindows);
                }
            }
            catch (Exception ex)
            {
                // Không chặn app khởi động chỉ vì registry bị chính sách máy chặn ghi
                _log.Error("Không đồng bộ được mục khởi động cùng Windows.", ex);
            }
        }

        private void ShowMainWindow()
        {
            // Nền tảng khác Windows không có tray thật (NullTrayService) nên chỉ cần nhánh WINDOWS
            if (_mainWindow is null) return;
            // Window đã hiện lại (đường tray OpenRequested/double-click) — cho phép toast trong app (spec §2.3)
            _window.SetVisible(true);
#if WINDOWS
            if (GetAppWindow(_mainWindow) is { } appWindow)
            {
                appWindow.Show();
            }
            // Microsoft.Maui.Controls.Window không có Activate() — bring-to-front qua native WinUI window
            if (_mainWindow.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                native.Activate();
            }
#endif
        }

        /// <summary>
        /// Đánh dấu thoát thật (update install) — cho phép <c>OnMainWindowClosing</c> (Windows)
        /// đóng thay vì ẩn về khay. Đường update gọi qua <c>MauiAppShutdown.Quit()</c>;
        /// nếu close event WinUI có nhận được lần thoát này mà cờ chưa đặt, CloseToTray
        /// sẽ hủy đóng → app ẩn thay vì thoát, updater chờ 60s rồi fail (spec §7/§10).
        /// </summary>
        internal void MarkReallyExiting()
        {
#if WINDOWS
            _reallyExiting = true;
#endif
        }

        /// <summary>Thoát hẳn từ menu tray: dừng proxy trước, giải phóng icon, rồi quit.</summary>
        private async void ExitFromTray()
        {
#if WINDOWS
            _reallyExiting = true;
#endif
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _proxyHost.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error("Dừng proxy khi thoát app thất bại.", ex);
            }
            _tray.Dispose();
            Current?.Quit();
        }

        /// <summary>
        /// User bấm balloon tip (chỉ lúc nó còn trên màn hình ~6s — giới hạn của
        /// <c>BalloonTipClicked</c>, spec §2.3): mở cửa sổ như đường <c>OpenRequested</c>,
        /// rồi điều hướng tới Log panel đã lọc lỗi.
        /// </summary>
        /// <remarks>
        /// Phần navigation (<c>TryNavigateToErrorLog</c>) đi qua <c>Dispatcher.Dispatch</c> theo chủ
        /// đích: đưa nó ra khỏi callback balloon tip của WinForms và đặt vào hàng đợi message-queue
        /// của MAUI — chỗ an toàn để chạm <c>NavigationManager</c> của circuit Blazor. Dispatch
        /// KHÔNG phải vì khác thread: <c>NotificationActivated</c> vẫn bắn trên UI thread đã tạo
        /// <c>NotifyIcon</c>. Delegate dispatch chạy ngoài try/catch của method này nên
        /// <see cref="NavigateToErrorLogOrWarn"/> phải tự cách ly.
        /// </remarks>
        private void OnNotificationActivated()
        {
            try
            {
                ShowMainWindow();
                // Dispatch đưa navigation ra khỏi callback balloon tip của WinForms, vào hàng đợi
                // message-queue của MAUI — chỗ an toàn để chạm NavigationManager của circuit.
                var app = Application.Current;
                if (app is null)
                {
                    NavigateToErrorLogOrWarn();
                }
                else
                {
                    app.Dispatcher.Dispatch(NavigateToErrorLogOrWarn);
                }
            }
            catch (Exception ex)
            {
                // Warn: dòng Error Category=App sẽ tự nuôi alert mới mỗi 30s (§2.2.1)
                WarnSafe($"Không mở được Log panel từ notification: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Điều hướng tới Log panel đã lọc lỗi; báo Warn nếu circuit chưa render lần đầu (chưa
        /// <c>Capture()</c>) — khi đó chỉ mở cửa sổ, không queue navigation (spec §2.4).
        /// </summary>
        private void NavigateToErrorLogOrWarn()
        {
            try
            {
                if (!_nav.TryNavigateToErrorLog())
                {
                    _log.Warn("Notification được bấm trước khi UI sẵn sàng — chỉ mở cửa sổ.");
                }
            }
            catch (Exception ex)
            {
                // Warn: dòng Error Category=App sẽ tự nuôi alert mới mỗi 30s (§2.2.1)
                WarnSafe($"Không mở được Log panel từ notification: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Warn an toàn lúc shutdown: LogService đã dispose sẽ ném ở đây, và catch mà ném thì
        // lọt thẳng lên message loop MAUI (ngoài try/catch nào). Nuốt là cố ý.
        private void WarnSafe(string message)
        {
            try { _log.Warn(message); }
            catch { /* đã nuốt: không còn chỗ an toàn để báo lỗi */ }
        }

        private void StopProxyOnExit()
        {
            try
            {
                // ProcessExit không chờ async — blocking với timeout để không treo process
                _proxyHost.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Error("Dừng proxy khi process thoát thất bại.", ex);
            }

            try
            {
                // X-exit với closeToTray=false không đi qua ExitFromTray → phải dispose tray ở đây,
                // nếu không NotifyIcon chỉ được dọn khi process chết (ghost icon khi không có message pump).
                // Dispose đặt trước retention: lỗi retention không được phép bỏ sót tray.
                // TrayService.Dispose idempotent (_icon is null → return) nên trùng lặp với ExitFromTray vô hại.
                _tray.Dispose();
                // Cắt subscription AlertRaised trước rồi mới dừng aggregator: thứ tự này để
                // không còn subscriber nào nhận alert trong lúc aggregator đang đóng cụm cuối.
                _notifier.Dispose();
                // Dừng vòng lặp dọn log + sync model free + check update + gom lỗi trước khi process chết
                _alerts.Dispose();
                _retention.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                // Flush dòng dataset cuối trong queue rồi mới đóng file — không thì request 2xx
                // gần sát lúc thoát mất record (spec dataset §7.6: DisposeAsync flush + đóng file)
                _dataset.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                _freeSync.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                _updateCheck.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Error("Dừng worker nền khi process thoát thất bại.", ex);
            }
        }

#if WINDOWS
        // AppWindow đang gắn OnMainWindowClosing — lưu để gỡ trước khi gắn lại
        private AppWindow? _trayCloseAppWindow;

        // WinUI window đang gắn Activated — lưu để gỡ trước khi gắn lại (cùng pattern
        // _trayCloseAppWindow: HandlerChanged chạy lại khi MAUI tái tạo handler; subscribe
        // 2 lần → SetFocused lặp mỗi event và rò rỉ handler cũ)
        private Microsoft.UI.Xaml.Window? _activatedNativeWindow;

        private void AttachCloseToTray(Window window)
        {
            window.HandlerChanged += (_, _) =>
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                // HandlerChanged có thể chạy lại khi MAUI tái tạo handler — gỡ subscription cũ
                // trước khi gắn mới, nếu không Closing bị subscribe 2 lần → ẩn + log lặp mỗi lần đóng
                if (_trayCloseAppWindow is not null)
                {
                    _trayCloseAppWindow.Closing -= OnMainWindowClosing;
                }
                _trayCloseAppWindow = appWindow;
                appWindow.Closing += OnMainWindowClosing;
                // Cờ focus cho IDesktopState: sự kiện activation của WinUI là nguồn duy nhất (spec §2.3).
                // Lưu ý: WinUI 3 Window KHÔNG có event Deactivated như WPF — chỉ MỘT event Activated,
                // args.WindowActivationState = Deactivated khi mất focus, CodeActivated/PointerActivated khi có focus.
                if (_activatedNativeWindow is not null)
                {
                    _activatedNativeWindow.Activated -= OnWindowActivated;
                }
                _activatedNativeWindow = native;
                native.Activated += OnWindowActivated;
            };
        }

        private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args) =>
            _window.SetFocused(args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated);

        private void OnMainWindowClosing(AppWindow appWindow, AppWindowClosingEventArgs args)
        {
            // Thoát từ tray thì cho phép đóng; ngược lại closeToTray=true → hủy đóng, ẩn cửa sổ
            if (_reallyExiting || !_settings.CloseToTray) return;
            args.Cancel = true;
            appWindow.Hide();
            // Cửa sổ ẩn về khay → mọi alert phải đi qua notification desktop, không phải toast trong app
            _window.SetVisible(false);
            _log.Info("Đóng cửa sổ → thu về khay hệ thống.");
        }

        private static AppWindow? GetAppWindow(Window window)
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return null;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }
#endif
    }
}
