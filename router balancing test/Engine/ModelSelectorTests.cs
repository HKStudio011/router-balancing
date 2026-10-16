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
        _executions = new ExecutionList(_db.CreateFactory());
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
                "p", "m", RequestPriority.Normal, DateTimeOffset.UtcNow, null, default);
            Assert.IsType<TryEnterResult.Entered>(ok); // TryEnter trả Entered (accountId) — D-B6
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

    [Fact]
    public async Task TrySelect_TreeFallback_FirstUnitFull_ParksWithoutSkipping()
    {
        var full = SeedProvider("full", maxConcurrent: 1);
        var free = SeedProvider("free");
        await OccupyAsync(full, 1);
        var sut = CreateSut();
        // root FB: [unitRR(chỉ leaf full), leafFree] — unit full → park, KHÔNG nhảy leafFree.
        // Candidates ĐẶT free trước (flat FB sẽ pick free → sai) — chỉ tree FB mới park.
        var root = new ComboUnit("root", ComboMode.Fallback,
        [
            new ComboUnit("unit", ComboMode.RoundRobin, [new CandidateLeaf(Candidate(full))]),
            new CandidateLeaf(Candidate(free)),
        ]);
        var selection = new SelectionSuccess(
            [Candidate(free), Candidate(full)], ComboMode.Fallback, "root", root);

        Assert.Null(await sut.TrySelectAsync(selection, default)); // park — FB tôn trọng đơn vị đầu
        Assert.Equal(0, _executions.GetInFlight(free)); // leafFree không bị đụng tới
    }

    [Fact]
    public async Task TrySelect_TreeFallback_FirstUnitChild_DescendsByChildMode()
    {
        var u1 = SeedProvider("u1");
        var u2 = SeedProvider("u2");
        var other = SeedProvider("other");
        var sut = CreateSut();
        // root FB: [unitRR(u1,u2), leafOther] — descend unitRR, RR con tôn trọng (1 trong 2 lá).
        // Candidates ĐẶT other trước (flat FB sẽ pick other → sai) — chỉ tree FB mới descend unit.
        var root = new ComboUnit("root", ComboMode.Fallback,
        [
            new ComboUnit("unit", ComboMode.RoundRobin,
            [
                new CandidateLeaf(Candidate(u1)),
                new CandidateLeaf(Candidate(u2)),
            ]),
            new CandidateLeaf(Candidate(other)),
        ]);
        var selection = new SelectionSuccess(
            [Candidate(other), Candidate(u1), Candidate(u2)], ComboMode.Fallback, "root", root);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.NotNull(chosen);
        Assert.Contains(chosen.Provider.Id, new[] { u1, u2 }); // chọn lá trong unitRR, không nhảy leafOther
    }

    [Fact]
    public async Task TrySelect_TreeRoundRobin_FullLeafAndFullUnit_AreSkipped()
    {
        // ghost: chỉ có trong flat Candidates (KHÔNG trong cây) — flat RR sẽ pick ghost (id thấp nhất,
        // inFlight 0) → test FAIL nếu selector chưa tree-aware.
        var ghost = SeedProvider("ghost");
        var full = SeedProvider("full", maxConcurrent: 1);
        var freeA = SeedProvider("freeA");
        var freeB = SeedProvider("freeB");
        var freeC = SeedProvider("freeC");
        await OccupyAsync(full, 1);
        var sut = CreateSut();
        // root RR: [leafFull, unitRR(freeA,freeB), leafFreeC] — full/skipped, còn unit + freeC
        var root = new ComboUnit("root", ComboMode.RoundRobin,
        [
            new CandidateLeaf(Candidate(full)),
            new ComboUnit("unit", ComboMode.RoundRobin,
            [
                new CandidateLeaf(Candidate(freeA)),
                new CandidateLeaf(Candidate(freeB)),
            ]),
            new CandidateLeaf(Candidate(freeC)),
        ]);
        var selection = new SelectionSuccess(
            [Candidate(ghost), Candidate(full), Candidate(freeA), Candidate(freeB), Candidate(freeC)],
            ComboMode.RoundRobin, "root", root);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.NotNull(chosen);
        Assert.NotEqual(ghost, chosen.Provider.Id); // ghost không có trong cây — không được trả
        Assert.Contains(chosen.Provider.Id, new[] { freeA, freeB, freeC });
    }

    [Fact]
    public async Task TrySelect_TreeRoundRobin_TwoUnits_CursorRotatesAcrossUnits()
    {
        // ghost: chỉ có trong flat Candidates — flat RR quay có ghost → 2/2 split FAIL nếu chưa tree-aware
        var ghost = SeedProvider("ghost");
        var x1 = SeedProvider("x1");
        var y1 = SeedProvider("y1");
        var sut = CreateSut();
        // root RR: [unitX(1 lá), unitY(1 lá)] — cursor toàn cục quay đều 2 unit
        var root = new ComboUnit("root", ComboMode.RoundRobin,
        [
            new ComboUnit("unitX", ComboMode.RoundRobin, [new CandidateLeaf(Candidate(x1))]),
            new ComboUnit("unitY", ComboMode.RoundRobin, [new CandidateLeaf(Candidate(y1))]),
        ]);
        var selection = new SelectionSuccess(
            [Candidate(ghost), Candidate(x1), Candidate(y1)], ComboMode.RoundRobin, "root", root);

        var picks = new List<long>();
        for (var i = 0; i < 4; i++)
            picks.Add((await sut.TrySelectAsync(selection, default))!.Provider.Id);

        Assert.DoesNotContain(ghost, picks); // ghost không có trong cây
        Assert.Equal(2, picks.Count(p => p == x1)); // mỗi unit ~n/2 pick
        Assert.Equal(2, picks.Count(p => p == y1));
    }
}
