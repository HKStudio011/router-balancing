using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

/// <summary>
/// Spec §5.1: <see cref="RequestCancelService"/> — nguồn sự thật duy nhất cho cancel
/// (HTTP endpoint và nút Huỷ trong UI cùng dùng).
/// </summary>
public class RequestCancelServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RequestQueue _queue = new();
    private readonly ExecutionList _executions;

    public RequestCancelServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private RequestCancelService CreateSut() => new(_queue, _executions);

    /// <summary>Seed provider với N tài khoản enabled — pattern ExecutionListTests.</summary>
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

    private static ProxyRequest Req(string id)
        => new(id, RequestPriority.Normal, "m1", Encoding.UTF8.GetBytes("{}"), new DefaultHttpContext());

    [Fact]
    public async Task Cancel_WhenQueued_RemovesFromQueueAndCompletesWithCancelled()
    {
        var request = Req("req-queued");
        _queue.Enqueue(request);
        var sut = CreateSut();

        var result = sut.Cancel("req-queued");

        Assert.Equal(RequestCancelResult.Cancelled, result);
        Assert.False(_queue.Contains("req-queued"));
        Assert.IsType<DispatchOutcome.Cancelled>(await request.Completion.Task);
    }

    [Fact]
    public async Task Cancel_WhenServing_ReturnsNotCancellable()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        var entered = await _executions.TryEnterAsync(pid, "req-1", "p1", "gpt-4o-mini",
            RequestPriority.Normal, DateTimeOffset.UtcNow, null, default);
        Assert.IsType<TryEnterResult.Entered>(entered);

        var result = sut.Cancel("req-1");

        Assert.Equal(RequestCancelResult.NotCancellable, result);
        Assert.True(_executions.Contains("req-1")); // đang phục vụ — không được gỡ slot
    }

    [Fact]
    public void Cancel_UnknownId_ReturnsNotFound()
    {
        var sut = CreateSut();

        Assert.Equal(RequestCancelResult.NotFound, sut.Cancel("ghost"));
    }

    [Fact]
    public void Cancel_AfterAlreadyCancelled_ReturnsNotFound()
    {
        _queue.Enqueue(Req("req-once"));
        var sut = CreateSut();
        Assert.Equal(RequestCancelResult.Cancelled, sut.Cancel("req-once"));

        // Idempotent: đã gỡ khỏi queue → lần 2 trả NotFound, không huỷ 2 lần
        Assert.Equal(RequestCancelResult.NotFound, sut.Cancel("req-once"));
    }
}
