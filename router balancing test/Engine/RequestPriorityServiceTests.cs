using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

/// <summary>
/// <see cref="RequestPriorityService"/> — nguồn sự thật cho nút đổi ưu tiên trong
/// RequestDetailModal; cùng pattern với <see cref="RequestCancelServiceTests"/>.
/// </summary>
public class RequestPriorityServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RequestQueue _queue = new();
    private readonly ExecutionList _executions;

    public RequestPriorityServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private RequestPriorityService CreateSut() => new(_queue, _executions);

    /// <summary>Seed provider với N tài khoản enabled — pattern RequestCancelServiceTests.</summary>
    private long SeedProvider(string name, int maxConcurrent = 4, int accountCount = 1)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        for (var i = 1; i <= accountCount; i++)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = $"{name}-acc{i}",
                Enabled = true,
            });
        }
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private static ProxyRequest Req(string id, RequestPriority priority = RequestPriority.Normal)
        => new(id, priority, "m1", Encoding.UTF8.GetBytes("{}"), new DefaultHttpContext());

    [Fact]
    public void SetPriority_WhenQueued_UpdatesPriority()
    {
        _queue.Enqueue(Req("req-1"));
        var sut = CreateSut();

        var result = sut.SetPriority("req-1", RequestPriority.High);

        Assert.Equal(RequestPriorityResult.Updated, result);
        Assert.Contains(_queue.Snapshot(), r => r.Id == "req-1" && r.Priority == RequestPriority.High);
    }

    [Fact]
    public void SetPriority_SamePriority_ReturnsUpdatedIdempotent()
    {
        _queue.Enqueue(Req("req-1", RequestPriority.High));
        var sut = CreateSut();

        var result = sut.SetPriority("req-1", RequestPriority.High);

        Assert.Equal(RequestPriorityResult.Updated, result);
        Assert.Contains(_queue.Snapshot(), r => r.Id == "req-1" && r.Priority == RequestPriority.High);
    }

    [Fact]
    public void SetPriority_WhenQueuedToHighest_DemotesOtherHighest()
    {
        _queue.Enqueue(Req("old-highest", RequestPriority.Highest));
        _queue.Enqueue(Req("req-upgrade"));
        var sut = CreateSut();

        var result = sut.SetPriority("req-upgrade", RequestPriority.Highest);

        Assert.Equal(RequestPriorityResult.Updated, result);
        Assert.Contains(_queue.Snapshot(),
            r => r.Id == "old-highest" && r.Priority == RequestPriority.High); // luật 1-Highest
        Assert.Contains(_queue.Snapshot(),
            r => r.Id == "req-upgrade" && r.Priority == RequestPriority.Highest);
    }

    [Fact]
    public async Task SetPriority_WhenServing_ReturnsNotUpdatable()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        var entered = await _executions.TryEnterAsync(pid, "req-1", "p1", "gpt-4o-mini",
            RequestPriority.Normal, DateTimeOffset.UtcNow, null, default);
        Assert.IsType<TryEnterResult.Entered>(entered);

        var result = sut.SetPriority("req-1", RequestPriority.High);

        Assert.Equal(RequestPriorityResult.NotUpdatable, result);
    }

    [Fact]
    public void SetPriority_UnknownId_ReturnsNotFound()
    {
        var sut = CreateSut();

        Assert.Equal(RequestPriorityResult.NotFound, sut.SetPriority("ghost", RequestPriority.High));
    }

    [Fact]
    public void SetPriority_AfterRemoved_ReturnsNotFound()
    {
        _queue.Enqueue(Req("req-once"));
        var sut = CreateSut();
        Assert.True(_queue.TryRemove("req-once", out _));

        // Đã rời queue (bị dispatch hoặc huỷ) → không đổi được nữa
        Assert.Equal(RequestPriorityResult.NotFound, sut.SetPriority("req-once", RequestPriority.High));
    }
}
