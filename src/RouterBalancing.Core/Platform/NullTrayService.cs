namespace RouterBalancing.Core.Platform;

/// <summary>Không khay hệ thống trên nền tảng không phải Windows — no-op để DI luôn resolve.</summary>
public sealed class NullTrayService : ITrayService
{
    // Accessor rỗng tường minh: không phát event, không tạo warning CS0067
    public event Action? OpenRequested { add { } remove { } }

    public event Action? ExitRequested { add { } remove { } }

    // Accessor rỗng tường minh: không phát event, không tạo warning CS0067
    public event Action? NotificationActivated { add { } remove { } }

    public void Initialize()
    {
    }

    /// <summary>No-op: nền tảng không có khay/notification desktop thật.</summary>
    public void Notify(string title, string message)
    {
    }

    public void Dispose()
    {
    }
}
