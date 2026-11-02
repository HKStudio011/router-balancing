using RouterBalancing.Core.Platform;

namespace router_balancing.Services;

/// <summary>
/// Nguồn cờ cho <see cref="IDesktopState"/> — App.xaml.cs đặt cờ trên các đường
/// đóng/mở cửa sổ về khay (SetVisible) và sự kiện Activated của WinUI window
/// (SetFocused). Không P/Invoke GetForegroundWindow: 2 cờ app tự đặt là đủ để chọn kênh
/// toast-trong-app vs notification desktop (spec §2.3).
/// </summary>
public sealed class WindowState : IDesktopState
{
    private bool _visible = true;

    // Mặc định focused=false: chưa nhận activation event nào thì coi như chưa interactive,
    // để burst lỗi lúc khởi động đi qua kênh desktop thay vì toast trong app mà không ai thấy.
    private bool _focused;

    /// <summary>Tích hai cờ: chỉ interactive khi cửa sổ đang hiện VÀ đang focus.</summary>
    public bool IsInteractive => _visible && _focused;

    /// <summary>Đặt cờ hiển thị: false khi ẩn về khay (AppWindow.Hide), true khi ShowMainWindow.</summary>
    public void SetVisible(bool visible) => _visible = visible;

    /// <summary>Đặt cờ focus: từ event Activated của WinUI window — false khi WindowActivationState=Deactivated, còn lại (CodeActivated/PointerActivated) là có focus.</summary>
    public void SetFocused(bool focused) => _focused = focused;
}
