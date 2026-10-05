using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryStateTests
{
    [Fact]
    public void RetryState_OnNewRequest_HasNothingTriedAndNoLastFailure()
    {
        var state = new RetryState();

        Assert.False(state.HasTried);
        Assert.Equal(0, state.TriedCount);
        Assert.Null(state.LastFailure);
        Assert.False(state.IsTried(1, "m1"));
    }

    [Fact]
    public void MarkTried_SameModelOnTwoProviders_CountsTwoTries()
    {
        var state = new RetryState();

        state.MarkTried(1, "m1");
        state.MarkTried(2, "m1");

        Assert.True(state.HasTried);
        Assert.Equal(2, state.TriedCount);
        // Cặp (provider, model) là key — cùng model 2 provider vẫn failover độc lập
        Assert.True(state.IsTried(1, "m1"));
        Assert.True(state.IsTried(2, "m1"));
    }

    [Fact]
    public void IsTried_MarksOnlyTheExactProviderModelPair()
    {
        var state = new RetryState();
        state.MarkTried(1, "m1");

        Assert.True(state.IsTried(1, "m1"));
        Assert.False(state.IsTried(1, "m2"));
        Assert.False(state.IsTried(2, "m1"));
    }

    [Fact]
    public void RetryState_MarkAccountTried_IsScopedPerProvider()
    {
        var state = new RetryState();

        state.MarkAccountTried(1, 7);

        Assert.True(state.IsAccountTried(1, 7));
        // TK id=7 ở provider khác là 2 TK riêng — không bị ảnh hưởng chéo
        Assert.False(state.IsAccountTried(2, 7));
        Assert.False(state.IsAccountTried(1, 8));
        Assert.False(state.IsAccountTried(2, 8));
    }

    [Fact]
    public void RetryState_MarkProviderFailed_AndIsProviderFailed()
    {
        var state = new RetryState();

        Assert.False(state.IsProviderFailed(1));

        state.MarkProviderFailed(1);

        Assert.True(state.IsProviderFailed(1));
        Assert.False(state.IsProviderFailed(2));
    }

    [Fact]
    public void RetryState_RecordAttempt_IncrementsAttemptsAndAppendsTrail()
    {
        var state = new RetryState();

        Assert.Equal(0, state.Attempts);
        Assert.Empty(state.Trail);

        state.RecordAttempt("prov-a", "m1", "acc-1", status: null);
        state.RecordAttempt("prov-b", "m2", "acc-2", status: 429);

        Assert.Equal(2, state.Attempts);
        RetryState.AttemptRecord[] expected =
        [
            new("prov-a", "m1", "acc-1", null),
            new("prov-b", "m2", "acc-2", 429),
        ];
        Assert.Equal(expected, state.Trail);
        // Status=null = lỗi mạng (không có HTTP response) — spec §2.3
        Assert.Null(state.Trail[0].Status);
        Assert.Equal(429, state.Trail[1].Status);
    }
}
