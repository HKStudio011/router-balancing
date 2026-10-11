using RouterBalancing.Core.Update;

namespace router_balancing.Services;

/// <inheritdoc cref="IAppShutdown"/>
public sealed class MauiAppShutdown : IAppShutdown
{
    /// <summary>Đóng app MAUI — updater chờ process thoát rồi ghi đè file (spec §10).</summary>
    public void Quit() => Application.Current?.Quit();
}
