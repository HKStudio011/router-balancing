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
}
