using System.Net.Http.Headers;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Parse header <c>Retry-After</c> (delta-seconds | HTTP-date), clamp 0..3600s —
/// spec 3C §3.6. Dùng duy nhất làm floor <c>nextProbeAt</c> trong store/watchdog;
/// request KHÔNG bao giờ chờ (đã chốt Phương án 1).
/// </summary>
public static class RetryAfterParser
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(3600);

    /// <param name="headerValue">Giá trị header thô; <see langword="null"/> khi thiếu hoặc không parse được.</param>
    /// <param name="now">Mốc "bây giờ" để tính HTTP-date — inject để test không lệch đồng hồ.</param>
    /// <returns>Khoảng chờ trong [0, 3600]s; date quá khứ → 0; thiếu/thông lệ → <see langword="null"/>.</returns>
    public static TimeSpan? Parse(string? headerValue, DateTimeOffset now)
    {
        if (headerValue is null)
            return null;
        if (!RetryConditionHeaderValue.TryParse(headerValue, out var value) || value is null)
            return null;
        if (value.Delta is { } delta)
            return Clamp(delta);
        if (value.Date is { } date)
            return Clamp(date - now);
        return null;
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;
}
