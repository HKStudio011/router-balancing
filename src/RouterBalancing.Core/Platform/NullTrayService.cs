namespace RouterBalancing.Core.Platform;

/// <summary>Không khay hệ thống trên nền tảng không phải Windows — no-op để DI luôn resolve.</summary>
public sealed class NullTrayService : ITrayService
{
    // Accessor rỗng tường minh: không phát event, không tạo warning CS0067
    public event Action? OpenRequested { add { } remove { } }

    public event Action? ExitRequested { add { } remove { } }

    public void Initialize()
    {
    }

    public void Dispose()
    {
    }
}
