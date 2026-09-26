namespace RouterBalancing.Core.Platform;

/// <summary>Biểu tượng khay hệ thống — bấm đúp/mở → hiện cửa sổ, Exit → thoát app.</summary>
public interface ITrayService : IDisposable
{
    /// <summary>Yêu cầu hiện lại cửa sổ chính.</summary>
    event Action? OpenRequested;

    /// <summary>Yêu cầu thoát app hoàn toàn (khác với đóng cửa sổ về khay).</summary>
    event Action? ExitRequested;

    /// <summary>Tạo icon trên khay — gọi sau khi DI sẵn sàng; idempotent.</summary>
    void Initialize();
}
