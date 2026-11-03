using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Cầu nối giữa cửa sổ quota CẤP PROVIDER (<see cref="IProviderQuotaGate"/>, keyed by
/// providerId) và badge cooldown của Live Trace (map theo TÊN tài khoản, do
/// <see cref="Proxies.CooldownTracker"/> dựng cho proxy đang down). Gate chặn cả provider nên
/// mọi tài khoản của provider đó đều "không được gọi upstream tới lúc này" — badge dùng lại
/// đúng map/class của proxy để UI không cần markup hay CSS riêng.
/// </summary>
public static class QuotaCooldownBadge
{
    /// <summary>
    /// Gộp window quota vào map cooldown theo tên tài khoản. Cùng tên mà có cả hai nguồn thì
    /// giữ window DÀI HƠN — countdown đang chạy không được rút ngắn giữa chừng.
    /// </summary>
    /// <param name="blockedProviders">providerId → thời điểm hết window (từ <see cref="IProviderQuotaGate.SnapshotBlocked"/>).</param>
    /// <param name="providers">Provider kèm navigation <c>Accounts</c>; provider không có ở đây (chưa nạp cache, hoặc đã bị xóa) thì window bị bỏ qua.</param>
    /// <param name="proxyCooldowns">Map cooldown proxy hiện có — không bị mutate.</param>
    /// <returns>Map MỚI (an toàn để UI giữ như state immutable) — kể cả khi không có window nào.</returns>
    public static IReadOnlyDictionary<string, DateTimeOffset> MergeByAccount(
        IReadOnlyDictionary<long, DateTimeOffset> blockedProviders,
        IReadOnlyList<Provider> providers,
        IReadOnlyDictionary<string, DateTimeOffset> proxyCooldowns)
    {
        // Luôn copy thay vì trả lại map gốc: caller giữ map này làm state và có đường gỡ entry
        // hết hạn tại chỗ khi render (TryGetCooldown) — trả reference sẽ làm mutation rò ngược
        // vào nguồn dữ liệu.
        var merged = new Dictionary<string, DateTimeOffset>(
            proxyCooldowns.Count + blockedProviders.Count);
        foreach (var (accountName, downUntil) in proxyCooldowns)
            merged[accountName] = downUntil;

        foreach (var provider in providers)
        {
            if (!blockedProviders.TryGetValue(provider.Id, out var until))
                continue;

            foreach (var account in provider.Accounts)
            {
                if (!merged.TryGetValue(account.Name, out var current) || until > current)
                    merged[account.Name] = until;
            }
        }

        return merged;
    }
}
