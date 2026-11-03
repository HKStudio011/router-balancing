namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IProviderQuotaGate"/>
/// <remarks>
/// State in-memory theo tiến trình proxy (một instance desktop duy nhất — giống
/// <c>ClientKeyRateLimiter</c>), không record lịch sử: chỉ cần “provider X còn bị chặn tới
/// lúc nào + replay gì”. Đồng hồ inject qua <see cref="TimeProvider"/> để test ranh giới window.
/// Cửa sổ dài bao lâu là quyết định của caller (setting + Retry-After của upstream) — gate
/// chỉ giữ cơ chế window, không chứa chính sách.
/// </remarks>
public sealed class ProviderQuotaGate(TimeProvider time) : IProviderQuotaGate
{
    private readonly Dictionary<long, (DateTimeOffset Until, RetryState.Failure Failure)> _blocked = new();
    private readonly object _gate = new();

    /// <inheritdoc/>
    public void Report(long providerId, RetryState.Failure failure, TimeSpan fallbackTtl)
    {
        // RetryAfter đã được RetryAfterParser clamp [0, 3600]s ở đường parse header —
        // upstream nói trước thì nghe theo; không nói thì dùng setting.
        var ttl = failure.RetryAfter is { } after && after > TimeSpan.Zero ? after : fallbackTtl;
        if (ttl <= TimeSpan.Zero)
            return; // providerQuotaBlockSec = 0 → tắt cooldown, mọi request đi upstream thật

        lock (_gate)
        {
            var until = time.GetUtcNow() + ttl;
            // Attempt sau (account khác, cùng provider) không được rút ngắn window đang chạy.
            if (_blocked.TryGetValue(providerId, out var existing) && existing.Until > until)
                until = existing.Until;
            _blocked[providerId] = (until, failure);
        }
    }

    /// <inheritdoc/>
    public bool TryGetBlock(long providerId, out RetryState.Failure? block)
    {
        lock (_gate)
        {
            block = null;
            if (!_blocked.TryGetValue(providerId, out var entry))
                return false;

            var remaining = entry.Until - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                _blocked.Remove(providerId);
                return false;
            }

            block = entry.Failure with { RetryAfter = remaining };
            return true;
        }
    }

    /// <inheritdoc/>
    public void Clear(long providerId)
    {
        lock (_gate)
        {
            _blocked.Remove(providerId);
        }
    }

    /// <inheritdoc/>
    public int BlockedCount()
    {
        lock (_gate)
        {
            PruneExpired(time.GetUtcNow());
            return _blocked.Count;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, DateTimeOffset> SnapshotBlocked()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);

            var snapshot = new Dictionary<long, DateTimeOffset>(_blocked.Count);
            foreach (var (providerId, entry) in _blocked)
                snapshot[providerId] = entry.Until;
            return snapshot;
        }
    }

    /// <summary>
    /// Gỡ entry đã hết window. UI dựa vào "hết hạn = đã đóng" để tắt badge nên gate không
    /// được để entry chết sống lại trong count/snapshot. Gọi trong <see cref="_gate"/>.
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        List<long>? expired = null;
        foreach (var (providerId, entry) in _blocked)
        {
            if (entry.Until <= now)
                (expired ??= []).Add(providerId);
        }
        if (expired is null)
            return;

        foreach (var providerId in expired)
            _blocked.Remove(providerId);
    }
}
