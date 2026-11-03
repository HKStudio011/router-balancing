namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IProviderQuotaGate"/>
/// <remarks>
/// State in-memory theo tiến trình proxy (một instance desktop duy nhất — giống
/// <c>ClientKeyRateLimiter</c>), không record lịch sử: chỉ cần “provider X còn bị chặn tới
/// lúc nào + replay gì”. Đồng hồ inject qua <see cref="TimeProvider"/> để test ranh giới window.
/// </remarks>
public sealed class ProviderQuotaGate(TimeProvider time) : IProviderQuotaGate
{
    /// <summary>
    /// Cửa sổ mở khi upstream KHÔNG gửi <c>Retry-After</c>. OpenCode Zen free tier không gửi
    /// header này (probe 2026-11-03) nên đây là giá trị quyết định hành vi: 2 phút đủ để
    /// hàng chục request trong một burst agent không gọi upstream vô ích, mà vẫn tự thử lại
    /// khi pool free reset (theo ngày/giờ).
    /// </summary>
    public static readonly TimeSpan DefaultBlock = TimeSpan.FromSeconds(120);

    private readonly Dictionary<long, (DateTimeOffset Until, RetryState.Failure Failure)> _blocked = new();
    private readonly object _gate = new();

    /// <inheritdoc/>
    public void Report(long providerId, RetryState.Failure failure)
    {
        lock (_gate)
        {
            // RetryAfter đã được RetryAfterParser clamp [0, 3600]s ở đường parse header —
            // Zero/null nghĩa là upstream không nói gì → dùng cửa sổ mặc định.
            var ttl = failure.RetryAfter is { } after && after > TimeSpan.Zero ? after : DefaultBlock;
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
}
