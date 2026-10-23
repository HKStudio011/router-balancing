using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class TraceChipPlannerTests
{
    [Fact]
    public void Plan_WhenFiveAccountsRunning_ShowsFourAndReportsHiddenActive()
    {
        var referenced = Enumerable.Range(1, 5)
            .Select(i => new TraceAccountRef($"nvidia {i}", Running: true, HasError: false))
            .ToList();

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 5);

        // Bug 2026-10-23: TK thứ 5 bị cap cắt âm thầm vì chip "+N" chỉ đếm idle.
        Assert.Equal(4, plan.Shown.Count);
        Assert.Equal(1, plan.HiddenActive);
        Assert.Equal(1, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenMoreThanFiveAccountsRunning_OverflowCoversAllHidden()
    {
        var referenced = Enumerable.Range(1, 6)
            .Select(i => new TraceAccountRef($"nvidia {i}", Running: true, HasError: false))
            .ToList();

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 6);

        Assert.Equal(4, plan.Shown.Count);
        Assert.Equal(2, plan.HiddenActive);
        Assert.Equal(2, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenTenAccountsRunning_KeepsOverflowCountCorrect()
    {
        var referenced = Enumerable.Range(1, 10)
            .Select(i => new TraceAccountRef($"nvidia {i}", Running: true, HasError: false))
            .ToList();

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 10);

        Assert.Equal(4, plan.Shown.Count);
        Assert.Equal(6, plan.HiddenActive);
        Assert.Equal(6, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenHiddenActiveAndIdleBothExist_SumsBothIntoOverflow()
    {
        // 6 TK đang chạy được tham chiếu; provider có 8 TK → 2 TK chạy bị cap cắt + 2 TK idle.
        var referenced = Enumerable.Range(1, 6)
            .Select(i => new TraceAccountRef($"nvidia {i}", Running: true, HasError: false))
            .ToList();

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 8);

        Assert.Equal(4, plan.Shown.Count);
        Assert.Equal(2, plan.HiddenActive);
        Assert.Equal(4, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenRunningAccountsExceedCap_KeepsRunningBeforeIdle()
    {
        var referenced = new List<TraceAccountRef>
        {
            new("idle-a", Running: false, HasError: false),
            new("run-1", Running: true, HasError: false),
            new("run-2", Running: true, HasError: false),
            new("run-3", Running: true, HasError: false),
            new("run-4", Running: true, HasError: false),
            new("run-5", Running: true, HasError: false),
        };

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 6);

        Assert.Equal(new[] { "run-1", "run-2", "run-3", "run-4" }, plan.Shown.Select(a => a.Name));
        Assert.Equal(1, plan.HiddenActive);   // run-5
        Assert.Equal(2, plan.OverflowCount);  // run-5 + idle-a
    }

    [Fact]
    public void Plan_WhenProviderCacheMissing_StillReportsHiddenActive()
    {
        var referenced = Enumerable.Range(1, 5)
            .Select(i => new TraceAccountRef($"nvidia {i}", Running: true, HasError: false))
            .ToList();

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: null);

        Assert.Equal(1, plan.HiddenActive);
        Assert.Equal(1, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenAllReferencedAccountsFit_OverflowCountsOnlyIdleAccounts()
    {
        var referenced = new List<TraceAccountRef>
        {
            new("nvidia 1", Running: true, HasError: false),
            new("nvidia 2", Running: true, HasError: false),
            new("nvidia 3", Running: true, HasError: false),
        };

        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 5);

        Assert.Equal(3, plan.Shown.Count);
        Assert.Equal(0, plan.HiddenActive);
        Assert.Equal(2, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenProviderCacheStale_DoesNotReportNegativeOverflow()
    {
        var referenced = new List<TraceAccountRef>
        {
            new("nvidia 1", Running: true, HasError: false),
            new("nvidia 2", Running: true, HasError: false),
        };

        // Cache nói provider chỉ có 1 TK (stale) — không được trả overflow âm.
        var plan = TraceChipPlanner.Plan(referenced, providerAccountCount: 1);

        Assert.Equal(2, plan.Shown.Count);
        Assert.Equal(0, plan.OverflowCount);
    }

    [Fact]
    public void Plan_WhenMaxChipsIsNotPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TraceChipPlanner.Plan([], providerAccountCount: 5, maxChips: 0));
    }
}
