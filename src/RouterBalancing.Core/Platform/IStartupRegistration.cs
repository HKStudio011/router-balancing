namespace RouterBalancing.Core.Platform;

/// <summary>Đăng ký app tự khởi động cùng hệ điều hành khi máy đăng nhập.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
