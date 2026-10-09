namespace RouterBalancing.Core.Engine;

/// <summary>
/// Backoff chờ retry transient (spec transient-retry §2.1/§3.3):
/// <c>wait(n) = min(baseMs × 2^(n-1), 4000ms) + jitter uniform[0..25%]</c>.
/// N = retry number 1-based; n ≤ 1 → base (không nhân). Cap 4000ms giữ worst-case
/// tích lũy ~15s/5 retry; jitter 25% tránh nhiều client retry cùng lúc (thundering herd).
/// </summary>
public static class BackoffPolicy
{
    /// <summary>Trần backoff mỗi lần retry (ms) — spec §1.3: cap 4000ms/lần.</summary>
    private const int MaxBackoffMs = 4000;

    /// <summary>Phần jitter tối đa trên raw delay: uniform [0..25%] (spec §3.3).</summary>
    private const double MaxJitterRatio = 0.25;

    /// <summary>Giới hạn exponent khi shift — tránh tràn long dù đã cap 4000ms (an toàn số).</summary>
    private const int MaxExponent = 20;

    /// <summary>
    /// Tính thời gian chờ cho lần retry thứ <paramref name="retryNumber"/> bằng
    /// <see cref="Random.Shared"/> — dùng cho production (Task 6 DispatcherLoop).
    /// </summary>
    /// <param name="retryNumber">Số thứ tự retry (1-based); ≤ 1 trả về base không nhân.</param>
    /// <param name="baseMs">Đợi cơ sở (ms) — settings <c>transientBackoffBaseMs</c>, 250..4000.</param>
    /// <returns>Thời gian chờ gồm jitter; tối đa 1.25 × 4000ms.</returns>
    public static TimeSpan Delay(int retryNumber, int baseMs)
        => Delay(retryNumber, baseMs, Random.Shared);

    /// <summary>
    /// Tính thời gian chờ với RNG inject — overload test deterministic (cùng seed → cùng kết quả).
    /// </summary>
    /// <param name="retryNumber">Số thứ tự retry (1-based); ≤ 1 trả về base không nhân.</param>
    /// <param name="baseMs">Đợi cơ sở (ms) — settings <c>transientBackoffBaseMs</c>, 250..4000.</param>
    /// <param name="rng">Nguồn ngẫu nhiên cho jitter; truyền <see cref="Random.Shared"/> khi production.</param>
    /// <returns>Thời gian chờ gồm jitter; tối đa 1.25 × 4000ms.</returns>
    public static TimeSpan Delay(int retryNumber, int baseMs, Random rng)
    {
        // long + clamp exponent: 1L << (n-1) tràn ý nghĩa khi n lớn — cap 4000ms vô hiệu
        // giá trị đó nhưng vẫn phải tính đúng trước khi Math.Min cắt.
        var rawMs = retryNumber <= 1
            ? baseMs
            : Math.Min((long)baseMs * (1L << Math.Min(retryNumber - 1, MaxExponent)), MaxBackoffMs);

        // Jitter [0..25%] của raw — chống thundering herd (spec §1.3, §3.3).
        var jitter = rng.NextDouble() * MaxJitterRatio * rawMs;

        return TimeSpan.FromMilliseconds(rawMs + jitter);
    }
}
