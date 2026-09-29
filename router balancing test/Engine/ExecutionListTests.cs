using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ExecutionListTests : IDisposable
{
    private readonly TestDb _db = new();

    public ExecutionListTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, int maxConcurrent = 4)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ExecutionList CreateSut() => new(_db.CreateFactory());

    // static readonly (không phải property) — giá trị phải cố định giữa lúc Enter
    // và lúc assert, nếu re-evaluate UtcNow thì Assert.Equal(Enq, ...) luôn lệch.
    private static readonly DateTimeOffset Enq = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task TryEnter_WhenBelowMax_ReturnsTrueAndTracksEntryWithAllFields()
    {
        var pid = SeedProvider("p1", maxConcurrent: 2);
        var sut = CreateSut();

        var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "gpt-4o-mini",
            RequestPriority.High, Enq, default);

        Assert.True(ok);
        Assert.True(sut.Contains("req00001"));
        var entry = Assert.Single(sut.Snapshot());
        Assert.Equal("req00001", entry.RequestId);
        Assert.Equal(pid, entry.ProviderId);
        Assert.Equal("p1", entry.ProviderName);
        Assert.Equal("gpt-4o-mini", entry.Model);
        Assert.Equal(RequestPriority.High, entry.Priority);
        Assert.Equal(Enq, entry.EnqueuedAt);
        Assert.True(entry.StartedAt >= entry.EnqueuedAt);
    }

    [Fact]
    public async Task TryEnter_WhenAtMax_ReturnsFalse()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));

        Assert.False(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(sut.Contains("req00002"));
    }

    [Fact]
    public async Task TryEnter_WhenProviderMissing_ReturnsFalse()
    {
        var sut = CreateSut();

        // Provider không tồn tại → MaxConcurrent = 0 → không bao giờ enter (spec §2.1)
        Assert.False(await sut.TryEnterAsync(999, "req00001", "ghost", "m", RequestPriority.Normal, Enq, default));
    }

    [Fact]
    public async Task TryEnter_ReflectsLatestMaxConcurrentFromDb()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));

        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var p = await db.Providers.FirstAsync(x => x.Id == pid);
            p.MaxConcurrent = 2;
            await db.SaveChangesAsync();
        }

        // Không cache — giá trị mới nhất từ DB tại mỗi lần Enter (spec §2.1)
        Assert.True(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
    }

    [Fact]
    public async Task Exit_RemovesEntryAndFiresExitedOnce()
    {
        var pid = SeedProvider("p1");
        var sut = CreateSut();
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
        var fired = 0;
        sut.Exited += () => fired++;

        sut.Exit("req00001");

        Assert.Equal(1, fired);
        Assert.False(sut.Contains("req00001"));
        Assert.Equal(0, sut.GetInFlight(pid));
    }

    [Fact]
    public async Task Exit_UnknownId_DoesNotFireExited()
    {
        var pid = SeedProvider("p1");
        var sut = CreateSut();
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
        var fired = 0;
        sut.Exited += () => fired++;

        sut.Exit("ghost");

        Assert.Equal(0, fired);
        Assert.True(sut.Contains("req00001"));
    }

    [Fact]
    public async Task CanEnter_ChecksWithoutMutating()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();

        Assert.True(await sut.CanEnterAsync(pid, default));
        Assert.Equal(0, sut.GetInFlight(pid)); // check không mutate — selector dùng được nhiều lần

        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(await sut.CanEnterAsync(pid, default));
    }

    [Fact]
    public async Task Snapshot_ReturnsAllLiveEntriesAcrossProviders()
    {
        var p1 = SeedProvider("p1");
        var p2 = SeedProvider("p2");
        var sut = CreateSut();
        await sut.TryEnterAsync(p1, "req00001", "p1", "m1", RequestPriority.Normal, Enq, default);
        await sut.TryEnterAsync(p2, "req00002", "p2", "m2", RequestPriority.Highest, Enq, default);

        var snapshot = sut.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, e => e.RequestId == "req00001" && e.ProviderId == p1);
        Assert.Contains(snapshot, e => e.RequestId == "req00002" && e.Priority == RequestPriority.Highest);
        Assert.Equal(1, sut.GetInFlight(p1));
        Assert.Equal(1, sut.GetInFlight(p2));
    }
}
