#if WINDOWS
using System.Runtime.InteropServices;
#endif
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // Instance thứ 2 thoát luôn — trước khi tạo bất kỳ service nào.
            // Environment.Exit an toàn ở đây vì CreateMauiApp chạy trước khi window được tạo.
            if (!SingleInstanceGuard.TryAcquire())
            {
                Environment.Exit(0);
            }

            // Migration PHẢI chạy trước Build(): App ctor inject IProxyHost →
            // IAppSettingsService đọc bảng AppSettings khi còn chưa được migrate.
            if (!TryInitializeDatabase())
            {
                // Thoát mã lỗi khác 0 — không bao giờ chạy tiếp trên DB chưa migrate.
                Environment.Exit(1);
            }

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            // DB file trong AppData — không theo thư mục cài app (Program Files ghi chỉ đọc)
            builder.Services.AddDbContextFactory<RouterBalancingDbContext>(options =>
                options.UseSqlite($"Data Source={StoragePathProvider.GetDatabasePath()}"));

            builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
            builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();
            builder.Services.AddSingleton<ILogService, LogService>();
            builder.Services.AddSingleton<IProxyHost, ProxyHost>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }

        /// <summary>
        /// Chạy auto-migration. Nếu thất bại: ghi fallback độc lập DB (file thường), hiện dialog
        /// nếu cơ chế dialog khả dụng ở ngữ cảnh này, giải mutex single-instance và trả <see langword="false"/>
        /// để caller thoát mã lỗi — không cho app chạy tiếp với DB chưa migrate.
        /// </summary>
        private static bool TryInitializeDatabase()
        {
            try
            {
                DbInitializer.Initialize();
                return true;
            }
            catch (Exception ex)
            {
                // KHÔNG dùng ILogService/ILogger tại đây: mọi logger đều ghi vào chính SQLite DB
                // vừa migrate thất bại → circular failure (log sẽ hỏng theo hoặc mất trắng).
                // Fallback phải độc lập DB: startup-error.log dưới app data + dialog nếu chạy được.
                var errorLogPath = StartupErrorReporter.Report(ex, StartupErrorReporter.DefaultLogPath);
                ShowStartupErrorDialog(ex, errorLogPath);
                SingleInstanceGuard.Release();
                return false;
            }
        }

        /// <summary>
        /// Hiện lỗi khởi động cho người dùng. Chỉ có MessageBox (Win32) ở Windows —
        /// MAUI app không tham chiếu WindowsDesktop nên WinForms MessageBox không dùng được,
        /// và CreateMauiApp chạy trước khi window/platform lifecycle tồn tại.
        /// </summary>
        private static void ShowStartupErrorDialog(Exception exception, string? errorLogPath)
        {
#if WINDOWS
            var details = errorLogPath is null
                ? "The error log file could not be written either."
                : $"Details were written to:{Environment.NewLine}{errorLogPath}";
            MessageBoxW(
                IntPtr.Zero,
                $"router-balancing cannot start: database migration failed.{Environment.NewLine}{Environment.NewLine}" +
                $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"{details}{Environment.NewLine}{Environment.NewLine}" +
                "The application will now close.",
                "router-balancing",
                uType: 0x00000010 | 0x00010000 /* MB_ICONERROR | MB_SETFOREGROUND */);
#else
            // Không có cơ chế dialog nào gọi được trên Android/iOS/MacCatalyst ở điểm này
            // (chưa có Activity/Window): startup-error.log + exit code != 0 là fallback duy nhất.
#endif
        }

#if WINDOWS
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
#endif
    }
}
