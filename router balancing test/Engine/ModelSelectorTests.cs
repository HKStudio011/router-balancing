using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ModelSelectorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ExecutionList _executions;

    public ModelSelectorTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory(),
            new ManualRetryStore(new NullLog(), TimeProvider.System));
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
        provider.Models.Add(new Model { ModelId = $"m-{name}", Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = $"{name}-acc",
            Enabled = true,
        });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelCandidate Candidate(long providerId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = db.Providers.Include(p => p.Models).First(p => p.Id == providerId);
        return new ModelCandidate(provider, provider.Models[0]);
    }

    private async Task OccupyAsync(long providerId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var ok = await _executions.TryEnterAsync(providerId, $"req{providerId}-{i}",
                "p", "m", RequestPriority.Normal, DateTimeOffset.UtcNow, default);
            Assert.NotNull(ok); // TryEnter trả long? (accountId) — D-B6
        }
    }

    private ModelSelector CreateSut() => new(_executions);

    [Fact]
    public async Task TrySelect_RR_AllIdle_SelectsFirstByProviderIdWithCursorZero()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(a, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_PrefersIdleCandidateOverLoadedLowerId()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        await OccupyAsync(a, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_WhenAllLoaded_PicksLeastInFlight()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        await OccupyAsync(a, 2);
        await OccupyAsync(b, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_TieRotatesByCursor()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var picks = new List<long>();
        for (var i = 0; i < 4; i++)
            picks.Add((await sut.TrySelectAsync(selection, default))!.Provider.Id);

        Assert.Equal([a, b, a, b], picks);
    }

    [Fact]
    public async Task TrySelect_RR_SkipsCandidateAtCapacity()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b");
        await OccupyAsync(a, 1); // a đầy
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_AllAtCapacity_ReturnsNullToPark()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b", maxConcurrent: 1);
        await OccupyAsync(a, 1);
        await OccupyAsync(b, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        Assert.Null(await sut.TrySelectAsync(selection, default));
    }

    [Fact]
    public async Task TrySelect_Fallback_ParksWhenFirstCandidateFull_WithoutJumping()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.Fallback);

        var first = await sut.TrySelectAsync(selection, default);
        Assert.Equal(a, first!.Provider.Id);

        await OccupyAsync(a, 1); // vị trí đầu đầy
        Assert.Null(await sut.TrySelectAsync(selection, default)); // park chờ a — không nhảy sang b
        Assert.Equal(0, _executions.GetInFlight(b)); // b không bị đụng tới
    }
}
