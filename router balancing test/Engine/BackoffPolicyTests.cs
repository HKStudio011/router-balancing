using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class BackoffPolicyTests
{
    [Theory]
    [InlineData(1, 1000, 1000, 1250)]   // n=1: base, không nhân
    [InlineData(2, 1000, 2000, 2500)]   // n=2: ×2
    [InlineData(3, 1000, 4000, 5000)]   // n=3: ×4, chạm cap 4000 + jitter ≤25%
    [InlineData(4, 1000, 4000, 5000)]   // n=4: cap giữ nguyên
    [InlineData(10, 1000, 4000, 5000)]  // n lớn: vẫn cap
    [InlineData(0, 1000, 1000, 1250)]   // n ≤ 1 → base
    [InlineData(-3, 1000, 1000, 1250)]
    public void Delay_ExponentialCappedWithJitter_ReturnsInRange(int n, int baseMs, int low, int high)
    {
        var rng = new Random(42);
        var wait = BackoffPolicy.Delay(n, baseMs, rng);
        Assert.InRange(wait.TotalMilliseconds, low, high);
    }

    [Fact]
    public void Delay_SameSeed_ProducesSameWait()
    {
        var a = BackoffPolicy.Delay(2, 1000, new Random(7));
        var b = BackoffPolicy.Delay(2, 1000, new Random(7));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Delay_DefaultOverload_UsesSharedRandom_InRange()
    {
        var wait = BackoffPolicy.Delay(1, 250);
        Assert.InRange(wait.TotalMilliseconds, 250, 312.5);
    }

    [Fact]
    public void Delay_JitterPresentAndCapAppliedToRawOnly_ReturnsStrictlyAboveRawMs()
    {
        // Seed 42: NextDouble() đầu tiên = 0.668 > 0 → jitter > 0 tất định, assertion chặt
        // (không phải biên inclusive như InRange) nên fail nếu jitter bị xoá
        // hoặc cap áp SAU jitter (n=3 sẽ kẹp về đúng 4000) — 2 property spec §3.3 binding.
        var firstRetry = BackoffPolicy.Delay(1, 1000, new Random(42));
        var cappedRetry = BackoffPolicy.Delay(3, 1000, new Random(42));

        Assert.True(firstRetry.TotalMilliseconds > 1000,
            $"n=1 phải là base + jitter > 1000, nhận {firstRetry.TotalMilliseconds}");
        Assert.True(cappedRetry.TotalMilliseconds > 4000,
            $"n=3 phải là 4000 + jitter > 4000 (cap áp lên raw trước jitter), nhận {cappedRetry.TotalMilliseconds}");
    }
}
