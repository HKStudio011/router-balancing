using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class ClientKeyRateLimiterTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryEnter_BelowRpm_AllowsAndCounts()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));

        Assert.True(limiter.TryEnter(1, ratePerMinute: 2, tokensPerMinute: null).Allowed);
        Assert.True(limiter.TryEnter(1, ratePerMinute: 2, tokensPerMinute: null).Allowed);
    }

    [Fact]
    public void TryEnter_ExceedingRpm_DeniesWithRetryAfter()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.TryEnter(1, 1, null);

        var (allowed, retryAfter) = limiter.TryEnter(1, 1, null);

        Assert.False(allowed);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public void TryEnter_AfterWindowRollover_AllowsAgain()
    {
        var time = new FakeTime(Start);
        var limiter = new ClientKeyRateLimiter(time);
        limiter.TryEnter(1, 1, null);
        Assert.False(limiter.TryEnter(1, 1, null).Allowed);

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.TryEnter(1, 1, null).Allowed);
    }

    [Fact]
    public void TryEnter_WhenRpmNull_NeverLimits()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.TryEnter(1, null, null).Allowed);
    }

    [Fact]
    public void TryEnter_WhenWindowTokensReachTpm_Denies()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 5);

        var (allowed, retryAfter) = limiter.TryEnter(1, null, tokensPerMinute: 5);

        Assert.False(allowed);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public void TryEnter_WhenWindowTokensBelowTpm_Allows()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 4);

        Assert.True(limiter.TryEnter(1, null, 5).Allowed);
    }

    [Fact]
    public void TryEnter_KeysAreIndependent()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.TryEnter(1, 1, null);

        Assert.False(limiter.TryEnter(1, 1, null).Allowed);
        Assert.True(limiter.TryEnter(2, 1, null).Allowed);
    }

    [Fact]
    public void AddTokens_AccumulatesWithinWindow()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 3);
        limiter.AddTokens(1, 4);

        Assert.False(limiter.TryEnter(1, null, 7).Allowed);
        Assert.True(limiter.TryEnter(1, null, 8).Allowed);
    }
}
