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
            DbInitializer.Initialize();

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
    }
}
