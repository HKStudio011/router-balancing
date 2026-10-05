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

    /// <summary>Seed provider với N tài khoản enabled — accountCount = 0 để test sentinel (D-B4+).</summary>
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

    private ExecutionList CreateSut() => new(_db.CreateFactory());

    // static readonly (không phải property) — giá trị phải cố định giữa lúc Enter
    // và lúc assert, nếu re-evaluate UtcNow thì Assert.Equal(Enq, ...) luôn lệch.
    private static readonly DateTimeOffset Enq = DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>Assert kết quả vào được và trả AccountId — thay pattern <c>Assert.NotNull</c> + <c>.Value</c> của <c>long?</c> cũ.</summary>
    private static long EnteredId(TryEnterResult result) =>
        Assert.IsType<TryEnterResult.Entered>(result).AccountId;

    [Fact]
    public async Task TryEnter_WhenBelowMax_ReturnsAccountIdAndTracksEntryWithAllFields()
    {
        var pid = SeedProvider("p1", maxConcurrent: 2);
        var sut = CreateSut();
        var accountId = EnabledAccountIds(pid).Single();

        var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "gpt-4o-mini",
            RequestPriority.High, Enq, null, default);

        var entered = Assert.IsType<TryEnterResult.Entered>(ok);
        Assert.Equal(accountId, entered.AccountId); // trả đúng TK được chọn, không chỉ bool (D-B6)
        Assert.True(sut.Contains("req00001"));
        var entry = Assert.Single(sut.Snapshot());
        Assert.Equal("req00001", entry.RequestId);
        Assert.Equal(pid, entry.ProviderId);
        Assert.Equal("p1", entry.ProviderName);
        Assert.Equal("gpt-4o-mini", entry.Model);
        Assert.Equal(RequestPriority.High, entry.Priority);
        Assert.Equal(Enq, entry.EnqueuedAt);
        Assert.True(entry.StartedAt >= entry.EnqueuedAt);
        Assert.Equal(accountId, entry.AccountId); // chiều TK cho đếm/log/snapshot (D-B3)
        Assert.Equal("p1-acc1", entry.AccountName);
    }

    [Fact]
    public async Task TryEnter_WhenAtMax_ReturnsFull()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));

        Assert.IsType<TryEnterResult.Full>(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));
        Assert.False(sut.Contains("req00002"));
    }

    [Fact]
    public async Task TryEnter_WhenProviderMissing_ReturnsFull()
    {
        var sut = CreateSut();

        // Provider không tồn tại → Full (park) — 0 KHÔNG được làm sentinel vì 0 = unlimited (D-B7)
        Assert.IsType<TryEnterResult.Full>(await sut.TryEnterAsync(999, "req00001", "ghost", "m", RequestPriority.Normal, Enq, null, default));
        Assert.False(sut.Contains("req00001"));
    }

    [Fact]
    public async Task TryEnter_ReflectsLatestMaxConcurrentFromDb()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));
        Assert.IsType<TryEnterResult.Full>(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));

        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var p = await db.Providers.FirstAsync(x => x.Id == pid);
            p.MaxConcurrent = 2;
            await db.SaveChangesAsync();
        }

        // Không cache — giá trị mới nhất từ DB tại mỗi lần Enter (spec §2.1)
        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));
    }

    [Fact]
    public async Task TryEnter_TwoAccounts_SkipsSaturatedAccount()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1, accountCount: 2);
        var sut = CreateSut();

        var first = EnteredId(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));
        var second = EnteredId(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));

        // TK1 đầy (N=1) → request 2 nhảy sang TK2, không bị chặn (D-B4.2)
        Assert.NotEqual(first, second);
        Assert.Equal(2, sut.Snapshot().Select(e => e.AccountId).Distinct().Count());
    }

    [Fact]
    public async Task TryEnter_TwoAccounts_BalancesLeastInFlight_TieRotatesByCursor()
    {
        var pid = SeedProvider("p1", maxConcurrent: 2, accountCount: 2);
        var sut = CreateSut();

        var first = EnteredId(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));
        var second = EnteredId(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));
        var third = EnteredId(await sut.TryEnterAsync(pid, "req00003", "p1", "m", RequestPriority.Normal, Enq, null, default));

        // Least-in-flight: req2 thấy TK1 (1) > TK2 (0) → TK2;
        // req3 tie 1-1 → xoay theo cursor → TK1
        Assert.NotEqual(first, second);
        Assert.Equal(first, third);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task TryEnter_SequentialRequests_RotatesEvenlyAcrossAccounts(int maxConcurrent)
    {
        var pid = SeedProvider("p1", maxConcurrent, accountCount: 2);
        var accounts = EnabledAccountIds(pid);
        var sut = CreateSut();
        var counts = new Dictionary<long, int>();

        for (var i = 0; i < 20; i++)
        {
            var chosen = EnteredId(await sut.TryEnterAsync(pid, $"req{i:D2}", "p1", "m",
                RequestPriority.Normal, Enq, null, default));
            counts[chosen] = counts.GetValueOrDefault(chosen) + 1;
            sut.Exit($"req{i:D2}");
        }

        // Request tuần tự (không chồng chéo) vẫn phải xoay đều — không dồn 1 TK
        Assert.Equal(10, counts[accounts[0]]);
        Assert.Equal(10, counts[accounts[1]]);
    }

    [Fact]
    public async Task TryEnter_ConcurrentRequests_DistributeEvenlyAcrossAccounts()
    {
        var pid = SeedProvider("p1", maxConcurrent: 0, accountCount: 2);
        var accounts = EnabledAccountIds(pid);
        var sut = CreateSut();
        var counts = new Dictionary<long, int>();

        for (var i = 0; i < 6; i++)
        {
            var chosen = EnteredId(await sut.TryEnterAsync(pid, $"req{i}", "p1", "m",
                RequestPriority.Normal, Enq, null, default));
            counts[chosen] = counts.GetValueOrDefault(chosen) + 1;
        }

        // Đồng thời: ít in-flight thắng → chênh lệch tối đa 1 (Max=0 = unlimited/TK)
        Assert.Equal(3, counts[accounts[0]]);
        Assert.Equal(3, counts[accounts[1]]);
    }

    [Fact]
    public async Task TryEnter_AllAccountsFull_ReturnsFull()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1, accountCount: 2);
        var sut = CreateSut();
        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));
        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, null, default));

        // Mọi TK enabled đều đầy → park (Full), không tạo entry (D-B4.4)
        Assert.IsType<TryEnterResult.Full>(await sut.TryEnterAsync(pid, "req00003", "p1", "m", RequestPriority.Normal, Enq, null, default));
        Assert.False(sut.Contains("req00003"));
    }

    [Fact]
    public async Task TryEnter_ZeroMax_ConcurrentUnlimited()
    {
        var pid = SeedProvider("p1", maxConcurrent: 0);
        var sut = CreateSut();

        for (var i = 0; i < 5; i++)
        {
            Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, $"req{i}", "p1", "m",
                RequestPriority.Normal, Enq, null, default));
        }

        Assert.Equal(5, sut.GetInFlight(pid)); // 0 = không giới hạn (D-B1)
    }

    [Fact]
    public async Task TryEnter_WhenNoEnabledAccounts_ReturnsSentinelZeroAndCreatesEntry()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 0);
        var sut = CreateSut();

        var sentinel = Assert.IsType<TryEnterResult.Entered>(
            await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));

        // Sentinel 0: provider OK nhưng 0 TK enabled → entry vẫn tạo để forward trả 503,
        // không park vô hạn khi user chưa bật TK nào (deviation V1 / D-B4+)
        Assert.Equal(0, sentinel.AccountId);
        var entry = Assert.Single(sut.Snapshot());
        Assert.Equal(0, entry.AccountId);
        Assert.Equal(string.Empty, entry.AccountName);
    }

    [Fact]
    public async Task TryEnter_SkipsDisabledAccount_UsesEnabledOne()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var first = await db.ProviderAccounts
                .Where(a => a.ProviderId == pid)
                .OrderBy(a => a.Id)
                .FirstAsync();
            first.Enabled = false;
            await db.SaveChangesAsync();
        }
        var enabledId = EnabledAccountIds(pid).Single();
        var sut = CreateSut();

        var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default);

        Assert.Equal(enabledId, EnteredId(ok)); // TK tắt không bao giờ được chọn (D-B4.1)
    }

    [Fact]
    public async Task Exit_RemovesEntryAndFiresExitedOnce()
    {
        var pid = SeedProvider("p1");
        var sut = CreateSut();
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default);
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
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default);
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

        Assert.IsType<TryEnterResult.Entered>(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, null, default));
        Assert.False(await sut.CanEnterAsync(pid, default));
    }

    [Fact]
    public async Task CanEnter_WhenNoEnabledAccounts_ReturnsTrue()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 0);
        var sut = CreateSut();

        // Sentinel (V1): CanEnter true để selector chọn → TryEnter tạo entry → forward 503
        Assert.True(await sut.CanEnterAsync(pid, default));
    }

    [Fact]
    public async Task Snapshot_ReturnsAllLiveEntriesAcrossProviders()
    {
        var p1 = SeedProvider("p1");
        var p2 = SeedProvider("p2");
        var sut = CreateSut();
        await sut.TryEnterAsync(p1, "req00001", "p1", "m1", RequestPriority.Normal, Enq, null, default);
        await sut.TryEnterAsync(p2, "req00002", "p2", "m2", RequestPriority.Highest, Enq, null, default);

        var snapshot = sut.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, e => e.RequestId == "req00001" && e.ProviderId == p1);
        Assert.Contains(snapshot, e => e.RequestId == "req00002" && e.Priority == RequestPriority.Highest);
        Assert.Equal(1, sut.GetInFlight(p1));
        Assert.Equal(1, sut.GetInFlight(p2));
    }

    [Fact]
    public async Task TryEnterAsync_WhenAccountExcluded_DoesNotPickItAgain()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
        var sut = CreateSut();
        var first = EnteredId(await sut.TryEnterAsync(pid, "req00001", "p1", "m",
            RequestPriority.Normal, Enq, null, default));

        var second = await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq,
            new HashSet<long> { first }, default);

        // TK đã exclude (đã thử trong request này) không được chọn lại — Account-advance (§2.2)
        Assert.NotEqual(first, EnteredId(second));
    }

    [Fact]
    public async Task TryEnterAsync_WhenUntriedAccountsAllFull_ReturnsFull()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1, accountCount: 2);
        var sut = CreateSut();
        var excluded = EnteredId(await sut.TryEnterAsync(pid, "req00001", "p1", "m",
            RequestPriority.Normal, Enq, null, default));
        EnteredId(await sut.TryEnterAsync(pid, "req00002", "p1", "m",
            RequestPriority.Normal, Enq, null, default)); // lấp TK còn lại

        // Còn TK chưa thử (TK2) nhưng tất cả đầy → Full (park), KHÔNG phải NoAccountLeft
        var result = await sut.TryEnterAsync(pid, "req00003", "p1", "m", RequestPriority.Normal, Enq,
            new HashSet<long> { excluded }, default);

        Assert.IsType<TryEnterResult.Full>(result);
        Assert.False(sut.Contains("req00003")); // không tạo entry khi park
    }

    [Fact]
    public async Task TryEnterAsync_WhenAllEnabledAccountsExcluded_ReturnsNoAccountLeft()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
        var allAccounts = EnabledAccountIds(pid).ToHashSet();
        var sut = CreateSut();

        var result = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq,
            allAccounts, default);

        // Mọi TK enabled đều đã thử → NoAccountLeft (advance candidate), không tạo entry (§2.2)
        Assert.IsType<TryEnterResult.NoAccountLeft>(result);
        Assert.False(sut.Contains("req00001"));
    }

    [Fact]
    public async Task TryEnterAsync_WhenNoEnabledAccounts_ReturnsSentinelZero()
    {
        var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 0);
        var sut = CreateSut();

        var result = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq,
            null, default);

        var entered = Assert.IsType<TryEnterResult.Entered>(result);
        Assert.Equal(0, entered.AccountId); // sentinel 0 = forward 503 (V1)
        Assert.Single(sut.Snapshot());
    }

    [Fact]
    public async Task TryEnterAsync_WhenProviderMissing_ReturnsFull()
    {
        var sut = CreateSut();

        var result = await sut.TryEnterAsync(999, "req00001", "ghost", "m", RequestPriority.Normal,
            Enq, null, default);

        Assert.IsType<TryEnterResult.Full>(result);
        Assert.False(sut.Contains("req00001"));
    }

    [Fact]
    public async Task TryEnterAsync_WithoutExclusion_RoundRobinsAsBefore()
    {
        var pid = SeedProvider("p1", maxConcurrent: 2, accountCount: 2);
        var accounts = EnabledAccountIds(pid);
        var sut = CreateSut();
        var counts = new Dictionary<long, int>();

        for (var i = 0; i < 20; i++)
        {
            var chosen = EnteredId(await sut.TryEnterAsync(pid, $"req{i:D2}", "p1", "m",
                RequestPriority.Normal, Enq, null, default));
            counts[chosen] = counts.GetValueOrDefault(chosen) + 1;
            sut.Exit($"req{i:D2}");
        }

        // exclude = null (dispatch đầu) → hành vi RR cũ giữ nguyên, không đổi Behavior
        Assert.Equal(10, counts[accounts[0]]);
        Assert.Equal(10, counts[accounts[1]]);
    }

    private List<long> EnabledAccountIds(long providerId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.ProviderAccounts.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.Enabled)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .ToList();
    }
}
