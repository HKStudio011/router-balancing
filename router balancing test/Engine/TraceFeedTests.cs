using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class TraceFeedTests
{
    private static TraceEvent Ev(string id, TraceStage stage, string model, DateTimeOffset at) =>
        new(id, stage, model, null, null, null, null, null, at);

    [Fact]
    public void Publish_RaisesPublished_WithEvent()
    {
        var feed = new TraceFeed(new NullLog());
        TraceEvent? received = null;
        feed.Published += e => received = e;

        var ev = Ev("r1", TraceStage.Received, "m1", DateTimeOffset.UnixEpoch);
        feed.Publish(ev);

        Assert.Same(ev, received);
    }

    [Fact]
    public void Snapshot_LateSubscriber_GetsRecentEvents_ReturnsLast200()
    {
        var feed = new TraceFeed(new NullLog());
        for (var i = 0; i < 250; i++)
            feed.Publish(Ev($"r{i}", TraceStage.Received, $"m{i + 1}", DateTimeOffset.UnixEpoch.AddSeconds(i)));

        var snap = feed.Snapshot();

        Assert.Equal(200, snap.Count);
        Assert.Equal("m51", snap[0].Model);
    }

    [Fact]
    public void ActiveSnapshot_RequestWithoutTerminal_IncludesId_AfterTerminalRemovesIt()
    {
        var feed = new TraceFeed(new NullLog());

        feed.Publish(Ev("r1", TraceStage.Received, "m1", DateTimeOffset.UnixEpoch));
        Assert.Contains(feed.ActiveSnapshot(), e => e.RequestId == "r1");

        // Finished là terminal — phải remove khỏi active map
        feed.Publish(Ev("r1", TraceStage.Finished, "m1", DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.DoesNotContain(feed.ActiveSnapshot(), e => e.RequestId == "r1");

        // Canceled cũng là terminal — case 2
        feed.Publish(Ev("r2", TraceStage.Received, "m2", DateTimeOffset.UnixEpoch.AddSeconds(2)));
        Assert.Contains(feed.ActiveSnapshot(), e => e.RequestId == "r2");

        feed.Publish(Ev("r2", TraceStage.Canceled, "m2", DateTimeOffset.UnixEpoch.AddSeconds(3)));
        Assert.DoesNotContain(feed.ActiveSnapshot(), e => e.RequestId == "r2");
    }

    [Fact]
    public void Publish_DoesNotThrow_ToCaller()
    {
        var feed = new TraceFeed(new NullLog());
        var received = new List<TraceEvent>();
        // Subscriber lành subscribe trước subscriber ném — multicast delegate dừng ở
        // exception nên thứ tự này mới verify được "lần 2 vẫn raise bình thường".
        feed.Published += e => received.Add(e);
        feed.Published += _ => throw new InvalidOperationException("subscriber boom");

        var first = Ev("r1", TraceStage.Received, "m1", DateTimeOffset.UnixEpoch);
        var ex1 = Record.Exception(() => feed.Publish(first));
        Assert.Null(ex1);

        var second = Ev("r2", TraceStage.Received, "m2", DateTimeOffset.UnixEpoch.AddSeconds(1));
        var ex2 = Record.Exception(() => feed.Publish(second));
        Assert.Null(ex2);

        Assert.Equal(2, received.Count);
        Assert.Same(first, received[0]);
        Assert.Same(second, received[1]);
        // State vẫn được mutate đúng dù subscriber ném
        Assert.Equal(2, feed.Snapshot().Count);
    }

    [Fact]
    public void PurgeAll_ClearsState()
    {
        var feed = new TraceFeed(new NullLog());
        feed.Publish(Ev("r1", TraceStage.Received, "m1", DateTimeOffset.UnixEpoch));

        Assert.Single(feed.Snapshot());
        Assert.Single(feed.ActiveSnapshot());

        feed.PurgeAll();

        Assert.Empty(feed.Snapshot());
        Assert.Empty(feed.ActiveSnapshot());
    }

    [Fact]
    public async Task Publish_FromManyThreads_PreservesAllEvents()
    {
        var feed = new TraceFeed(new NullLog());

        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
                feed.Publish(Ev($"t{t}-r{i}", TraceStage.Received, $"m{t}-{i}", DateTimeOffset.UnixEpoch.AddMilliseconds(i)));
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(160, feed.Snapshot().Count);
    }
}
