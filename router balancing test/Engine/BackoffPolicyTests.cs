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
}
