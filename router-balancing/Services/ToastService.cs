namespace router_balancing.Services;

public enum ToastSeverity
{
    Info,
    Success,
    Error,
}

public sealed record ToastItem(Guid Id, string Message, ToastSeverity Severity);

/// <summary>Phát toast cho UI — Show từ bất kỳ đâu, MainLayout render và tự hủy sau vài giây.</summary>
public sealed class ToastService
{
    public event Action<ToastItem>? ToastAdded;

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info) =>
        ToastAdded?.Invoke(new ToastItem(Guid.NewGuid(), message, severity));
}
