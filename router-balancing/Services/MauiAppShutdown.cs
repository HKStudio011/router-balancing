using RouterBalancing.Core.Update;

namespace router_balancing.Services;

/// <inheritdoc cref="IAppShutdown"/>
public sealed class MauiAppShutdown : IAppShutdown
{
    /// <summary>Đóng app MAUI — updater chờ process thoát rồi ghi đè file (spec §10).</summary>
    public void Quit()
    {
        // Đặt cờ thoát thật TRƯỚC khi Quit: nếu AppWindow.Closing (WinUI) nhận được close
        // từ Application.Exit mà cờ chưa đặt, CloseToTray sẽ hủy → app ẩn về khay thay vì
        // thoát, updater chờ 60s timeout và snapshot bị restore oan ở boot sau (spec §7).
        if (Application.Current is App app)
        {
            app.MarkReallyExiting();
        }

        Application.Current?.Quit();
    }
}
