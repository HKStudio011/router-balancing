using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Platform;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
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
        private readonly FreeModelSyncWorker _freeSync;
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
            FreeModelSyncWorker freeSync)
        {
            InitializeComponent();
            _log = log;
            _proxyHost = proxyHost;
            _settings = settings;
            _startup = startup;
            _tray = tray;
            _retention = retention;
            _freeSync = freeSync;

            _log.Info("router-balancing khởi động.");

            // Dọn log quá hạn ngay khi mở app rồi lặp 24h — retention không cần user bấm
            _retention.Start();

            // Sync model free: trễ 60s rồi lặp 6h — preset free cần list model mới mà user không tự bấm
            _freeSync.Start();

            _tray.OpenRequested += ShowMainWindow;
            _tray.ExitRequested += ExitFromTray;
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
                // Dừng vòng lặp dọn log + sync model free trước khi process chết
                _retention.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                _freeSync.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Error("Dừng worker nền khi process thoát thất bại.", ex);
            }
        }

#if WINDOWS
        // AppWindow đang gắn OnMainWindowClosing — lưu để gỡ trước khi gắn lại
        private AppWindow? _trayCloseAppWindow;

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
            };
        }

        private void OnMainWindowClosing(AppWindow appWindow, AppWindowClosingEventArgs args)
        {
            // Thoát từ tray thì cho phép đóng; ngược lại closeToTray=true → hủy đóng, ẩn cửa sổ
            if (_reallyExiting || !_settings.CloseToTray) return;
            args.Cancel = true;
            appWindow.Hide();
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
