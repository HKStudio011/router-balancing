using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryStateTests
{
    [Fact]
    public void RetryState_OnNewRequest_HasNothingTriedAndNoLastRetryable()
    {
        var state = new RetryState();

        Assert.False(state.HasTried);
        Assert.Equal(0, state.TriedCount);
        Assert.Empty(state.TriedModels);
        Assert.Null(state.LastRetryable);
        Assert.False(state.IsTried(1, "m1"));
    }

    [Fact]
    public void MarkTried_SameModelOnTwoProviders_CountsTwoTriesButOneDistinctModel()
    {
        var state = new RetryState();

        state.MarkTried(1, "m1");
        state.MarkTried(2, "m1");

        Assert.True(state.HasTried);
        Assert.Equal(2, state.TriedCount);
        // Distinct theo model — RecordFailure +1/exhaustion theo model (Quyết định #4),
        // không phải theo lần thử
        Assert.Equal(new[] { "m1" }, state.TriedModels);
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
