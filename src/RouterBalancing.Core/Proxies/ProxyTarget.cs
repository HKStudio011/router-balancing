using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Mục tiêu proxy của request hiện tại — <see cref="AsyncLocal{T}"/> để
/// <see cref="ProxyHealthHandler"/> đọc mà không cần truyền qua ctor (spec §4.4).
/// <c>null</c> = request không qua assignment (path nội bộ/test) → handler giữ
/// behavior global pool (D7).
/// </summary>
public sealed record ProxyTarget(Provider Provider, ProviderAccount? Account)
{
    private static readonly AsyncLocal<ProxyTarget?> s_current = new();

    /// <summary>Request đang xử lý; reset <c>null</c> sau khi xong (finally).</summary>
    public static AsyncLocal<ProxyTarget?> Current => s_current;
}
