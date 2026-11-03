using Microsoft.AspNetCore.Components;

namespace router_balancing.Services;

/// <summary>
/// Cầm <see cref="NavigationManager"/> cho những nơi ngoài Blazor scope (App.xaml.cs) cần
/// điều hướng — hiện là đường bấm notification desktop (spec error-notify-client-name §2.4).
/// Singleton: <c>NavigationManager</c> trong MAUI Blazor Hybrid là service **scoped** theo
/// circuit, nên inject trực tiếp vào service singleton là bẫy DI (spec §2.3) — layout render
/// lần đầu gọi <see cref="Capture"/> để lại reference.
/// </summary>
public sealed class AppNavigation
{
    /// <summary>Đích duy nhất singleton này biết: Log panel đã lọc sẵn lỗi.</summary>
    private const string ErrorLogPath = "/logs?severity=Error";

    // Capture từ UI thread Blazor, TryNavigateToErrorLog đọc từ UI thread MAUI (WinForms
    // message pump) — reference assignment là atomic; volatile chỉ để thread đọc không thấy
    // giá trị cũ vĩnh viễn.
    private volatile NavigationManager? _nav;

    /// <summary>
    /// Ghi lại <see cref="NavigationManager"/> của circuit đang render — gọi từ
    /// <c>MainLayout.OnInitializedAsync</c> (điểm sớm nhất chắc chắn có NavigationManager).
    /// </summary>
    /// <param name="nav">NavigationManager của circuit hiện hành.</param>
    /// <remarks>
    /// Capture lặp lại (nhiều circuit / reconnect) là chủ đích: instance mới thắng, nếu không
    /// một circuit đã chết sẽ giữ lại NavigationManager không còn điều hướng được.
    /// </remarks>
    public void Capture(NavigationManager nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        _nav = nav;
    }

    /// <summary>
    /// Điều hướng tới Log panel với bộ lọc <c>Severity=Error</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> khi circuit chưa render lần đầu (chưa <see cref="Capture"/>) —
    /// caller chỉ mở cửa sổ, không queue navigation (chấp nhận mất một lần, spec §2.4).
    /// </returns>
    public bool TryNavigateToErrorLog()
    {
        var nav = _nav;
        if (nav is null)
        {
            return false;
        }

        nav.NavigateTo(ErrorLogPath);
        return true;
    }
}
