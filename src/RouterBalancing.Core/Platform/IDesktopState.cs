namespace RouterBalancing.Core.Platform;

/// <summary>
/// Trạng thái tương tác của cửa sổ app — nguồn cho <see cref="RouterBalancing.Core.Notifications.NotificationChannelPolicy"/>
/// chọn kênh toast trong app hay notification desktop (spec §2.3).
/// Core chỉ đọc cờ, không tự quan sát window: app layer (App.xaml.cs) là nơi duy nhất đặt cờ,
/// để không phải P/Invoke <c>GetForegroundWindow</c> hay polling.
/// </summary>
public interface IDesktopState
{
    /// <summary>
    /// true khi người dùng thực sự đang nhìn vào app (cửa sổ hiện VÀ đang focus).
    /// false khi thu về khay hoặc cửa sổ mở mà không focus → alert đi qua kênh desktop.
    /// </summary>
    bool IsInteractive { get; }
}
