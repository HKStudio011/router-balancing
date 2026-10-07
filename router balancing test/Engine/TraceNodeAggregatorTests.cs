using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class TraceNodeAggregatorTests
{
    [Fact]
    public void ReceivedOnExistingNode_MergesFieldsWithoutResettingStage()
    {
        var agg = new TraceNodeAggregator(TimeSpan.FromSeconds(10));
        var route = new TraceRoute(null, "p1", "a1");
        agg.Apply(new TraceEvent("r1", TraceStage.Attempt, "m1", route, 1, null, null, null, DateTimeOffset.Now));

        var node = agg.Apply(new TraceEvent("r1", TraceStage.Received, "m1",
            null, null, null, null, null, DateTimeOffset.Now,
            Priority: RequestPriority.High, HeadersSent: true));

        Assert.Equal(TraceStage.Attempt, node.Stage);        // KHÔNG reset — G2/G3 không kéo dot về queue
        Assert.Equal(RequestPriority.High, node.Priority);
        Assert.True(node.HeadersSent);
        Assert.Same(route, node.Route);                      // Route null trong event → giữ route đã biết
        Assert.Equal(TraceAnchor.Attempt, TraceNodeAggregator.AnchorOf(node));
    }

    [Fact]
    public void ParkedEvent_MovesNodeToQueueAnchor()
    {
        var agg = new TraceNodeAggregator(TimeSpan.FromSeconds(10));
        agg.Apply(new TraceEvent("r1", TraceStage.DispatchStarted, "m1", null, null, null, null, null, DateTimeOffset.Now));

        var node = agg.Apply(new TraceEvent("r1", TraceStage.Parked, "m1", null, null, null, null, null, DateTimeOffset.Now));

        Assert.Equal(TraceStage.Parked, node.Stage);
        Assert.Equal(TraceAnchor.Queue, TraceNodeAggregator.AnchorOf(node)); // dot chuyển về Hàng đợi
        Assert.Null(node.ExpiresAt);                           // Parked không terminal — node sống mãi
    }

    [Fact]
    public void PriorityTag_ReturnsAmberForHighest_BlueForHigh_NoneForNormal()
    {
        Assert.Equal("trace-tag--prio-highest", TraceNodeAggregator.PriorityTag(RequestPriority.Highest));
        Assert.Equal("trace-tag--prio-high", TraceNodeAggregator.PriorityTag(RequestPriority.High));
        Assert.Null(TraceNodeAggregator.PriorityTag(RequestPriority.Normal));
    }
}
