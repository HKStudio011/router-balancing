namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Correlation giữa pick của <see cref="ProxyHealthHandler"/> và
/// <see cref="RoundRobinWebProxy.GetProxy"/> — <c>IWebProxy.GetProxy(Uri)</c> chỉ nhận
/// destination nên dùng <see cref="AsyncLocal{T}"/> để biết request nào đang gọi (spec §4.2).
/// Không có scope (không qua handler) → bypass (direct) — deterministic, không RR ngầm.
/// </summary>
public static class ProxyContext
{
    private static readonly AsyncLocal<ProxyAttempt?> s_current = new();

    /// <summary>Proxy đang phục vụ request hiện tại — <see langword="null"/> = direct.</summary>
    public static ProxyAttempt? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
