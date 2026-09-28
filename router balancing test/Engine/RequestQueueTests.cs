using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RequestQueueTests
{
    private readonly RequestQueue _queue = new();

    private static ProxyRequest Req(string id, RequestPriority priority = RequestPriority.Normal)
        => new(id, priority, "m1", Encoding.UTF8.GetBytes("{}"), new DefaultHttpContext());

    [Fact]
    public void Enqueue_New_FiresChangedAndPeekReturnsFifoHead()
    {
        var changed = 0;
        _queue.Changed += () => changed++;

        Assert.True(_queue.Enqueue(Req("r1")));
        Assert.True(_queue.Enqueue(Req("r2")));
        Assert.Equal(2, changed);

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r1", head!.Id);
        // Peek là read-only — không fire Changed
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Enqueue_DuplicateId_ReturnsFalseWithoutStateChange()
    {
        Assert.True(_queue.Enqueue(Req("r1")));
        var changed = 0;
        _queue.Changed += () => changed++;

        Assert.False(_queue.Enqueue(Req("r1")));

        Assert.Equal(0, changed);
        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r1", head!.Id);
    }

    [Fact]
    public void Enqueue_HighPriority_DequeuesBeforeNormal()
    {
        _queue.Enqueue(Req("n1", RequestPriority.Normal));
        _queue.Enqueue(Req("n2", RequestPriority.Normal));
        _queue.Enqueue(Req("h1", RequestPriority.High));

        Assert.True(_queue.Take("h1", out var first));
        Assert.Equal("h1", first!.Id);
        Assert.True(_queue.Take("n1", out var second));
        Assert.Equal("n1", second!.Id);
    }

    [Fact]
    public void Enqueue_Highest_DemotesExistingHighestToHigh()
    {
        _queue.Enqueue(Req("old", RequestPriority.Highest));
        _queue.Enqueue(Req("new", RequestPriority.Highest));

        // Luật 1-Highest: item cũ downgrade xuống High (spec §3.1)
        Assert.True(_queue.Take("new", out var first));
        Assert.Equal("new", first!.Id);
        Assert.True(_queue.Take("old", out var second));
        Assert.Equal("old", second!.Id);
        Assert.Equal(RequestPriority.High, second.Priority);
        Assert.False(_queue.Contains("old"));
    }

    [Fact]
    public void Enqueue_WhenOnlyHighest_PeekReturnsItWithHighestPriority()
    {
        _queue.Enqueue(Req("only", RequestPriority.Highest));

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("only", head!.Id);
        Assert.Equal(RequestPriority.Highest, head.Priority);
    }

    [Fact]
    public void Peek_WhenQueueEmpty_ReturnsFalse()
    {
        Assert.False(_queue.Peek(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Take_RemovesRequestedItem_LeavingFollowingItem()
    {
        _queue.Enqueue(Req("r1"));
        _queue.Enqueue(Req("r2"));

        Assert.True(_queue.Take("r1", out var taken));
        Assert.Equal("r1", taken!.Id);
        Assert.False(_queue.Contains("r1"));

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r2", head!.Id);
    }

    [Fact]
    public void Take_WhenItemAlreadyRemoved_ReturnsFalse()
    {
        _queue.Enqueue(Req("r1"));

        Assert.True(_queue.TryRemove("r1", out _));
        Assert.False(_queue.Take("r1", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void TryRemove_UnknownId_ReturnsFalse()
    {
        Assert.False(_queue.TryRemove("ghost", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void SetPriority_PromoteNormalToHigh_ReordersIntoHighBucket()
    {
        _queue.Enqueue(Req("h1", RequestPriority.High));
        _queue.Enqueue(Req("n1", RequestPriority.Normal));
        _queue.Enqueue(Req("n2", RequestPriority.Normal));

        Assert.True(_queue.SetPriority("n2", RequestPriority.High));

        // n2 vào High bucket theo sequence cũ (3) nên đứng sau h1 (1) — nhưng vẫn trước n1
        Assert.True(_queue.Take("h1", out var first));
        Assert.True(_queue.Take("n2", out var second));
        Assert.Equal(RequestPriority.High, second!.Priority);
        Assert.True(_queue.Take("n1", out var third));
        Assert.Equal("n1", third!.Id);

        Assert.False(_queue.SetPriority("ghost", RequestPriority.High));
    }

    [Fact]
    public void SetPriority_PromoteToHighest_DemotesExistingHighest()
    {
        _queue.Enqueue(Req("h1", RequestPriority.Highest));
        _queue.Enqueue(Req("n1", RequestPriority.Normal));

        Assert.True(_queue.SetPriority("n1", RequestPriority.Highest));

        Assert.True(_queue.Take("n1", out var first));
        Assert.Equal("n1", first!.Id);
        Assert.True(_queue.Take("h1", out var second));
        Assert.Equal(RequestPriority.High, second!.Priority);
    }

    [Fact]
    public void Snapshot_ReturnsAllItemsInPriorityThenSequenceOrder()
    {
        _queue.Enqueue(Req("n1"));
        _queue.Enqueue(Req("h1", RequestPriority.Highest));
        _queue.Enqueue(Req("n2"));
        _queue.Enqueue(Req("h2", RequestPriority.High));

        var ids = _queue.Snapshot().Select(r => r.Id).ToArray();

        // Highest (1-Highest: h1 không bị demote vì không có item Highest mới vào sau)
        Assert.Equal(["h1", "h2", "n1", "n2"], ids);
    }

    [Fact]
    public void Contains_ReturnsTrueOnlyWhileItemInQueue()
    {
        _queue.Enqueue(Req("r1"));

        Assert.True(_queue.Contains("r1"));
        Assert.False(_queue.Contains("ghost"));

        _queue.Take("r1", out _);
        Assert.False(_queue.Contains("r1"));
    }
}
