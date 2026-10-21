using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Usage;

namespace router_balancing_test.Usage;

public class UsageDailyServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly TestDb _db = new();
    private readonly IUsageDailyService _usage;

    public UsageDailyServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _usage = new UsageDailyService(_db.CreateFactory(), new FixedTimeProvider(Now));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RecordAsync_AccumulatesRequestsAndTokensForSameDay()
    {
        var keyId = await SeedClientKeyAsync();

        await _usage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 1, tokens: 10);
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 2, tokens: 5);

        var totals = await _usage.GetTodayTotalsAsync(UsageSubjectKind.ClientKey, new[] { keyId });
        Assert.Equal(new UsageTotals(3, 15), totals[keyId]);
    }

    [Fact]
    public async Task RecordAsync_ForDifferentDays_CreatesSeparateRows()
    {
        var keyId = await SeedClientKeyAsync();
        var yesterdayUsage = new UsageDailyService(
            _db.CreateFactory(), new FixedTimeProvider(Now.AddDays(-1)));

        await yesterdayUsage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 5, tokens: 50);
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 1, tokens: 10);

        var todayTotals = await _usage.GetTodayTotalsAsync(UsageSubjectKind.ClientKey, new[] { keyId });
        Assert.Equal(new UsageTotals(1, 10), todayTotals[keyId]);

        await using var db = _db.CreateFactory().CreateDbContext();
        var rows = await db.UsageDaily
            .Where(u => u.SubjectKind == UsageSubjectKind.ClientKey && u.SubjectId == keyId)
            .OrderBy(u => u.Date)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-1).UtcDateTime), rows[0].Date);
        Assert.Equal(new UsageTotals(5, 50), new UsageTotals(rows[0].Requests, rows[0].Tokens));
        Assert.Equal(Today, rows[1].Date);
        Assert.Equal(new UsageTotals(1, 10), new UsageTotals(rows[1].Requests, rows[1].Tokens));
    }

    [Fact]
    public async Task RecordAsync_WhenSubjectMissing_DoesNotCreateOrphan()
    {
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, 999, requests: 1, tokens: 10);
        await _usage.RecordAsync(UsageSubjectKind.ProviderAccount, 999, requests: 1, tokens: 10);

        await using var db = _db.CreateFactory().CreateDbContext();
        Assert.Empty(await db.UsageDaily.ToListAsync());
    }

    [Fact]
    public async Task RecordAsync_ZeroValues_DoesNotCreateRow()
    {
        var keyId = await SeedClientKeyAsync();

        await _usage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 0, tokens: 0);

        await using var db = _db.CreateFactory().CreateDbContext();
        Assert.Empty(await db.UsageDaily.ToListAsync());
    }

    [Fact]
    public async Task GetTodayTotalsAsync_FiltersByKindAndRequestedIds()
    {
        var key1 = await SeedClientKeyAsync();
        var key2 = await SeedClientKeyAsync();
        var accountId = await SeedProviderAccountAsync();
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, key1, requests: 1, tokens: 10);
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, key2, requests: 2, tokens: 20);
        await _usage.RecordAsync(UsageSubjectKind.ProviderAccount, accountId, requests: 3, tokens: 30);

        var keys = await _usage.GetTodayTotalsAsync(UsageSubjectKind.ClientKey, new[] { key1 });
        var accounts = await _usage.GetTodayTotalsAsync(
            UsageSubjectKind.ProviderAccount, new[] { accountId });

        Assert.Equal(new UsageTotals(1, 10), keys[key1]);
        Assert.False(keys.ContainsKey(key2));
        Assert.Equal(new UsageTotals(3, 30), accounts[accountId]);
    }

    [Fact]
    public async Task RecordAsync_SameNumericIdDifferentKinds_KeepsSeparateRows()
    {
        // Ép client key dùng đúng Id của account để chứng minh SubjectKind nằm trong PK:
        // nếu thiếu kind, hai dòng sẽ ghi đè nhau.
        var accountId = await SeedProviderAccountAsync();
        var keyId = await SeedClientKeyAsync(accountId);
        Assert.Equal(accountId, keyId);

        await _usage.RecordAsync(UsageSubjectKind.ClientKey, keyId, requests: 1, tokens: 10);
        await _usage.RecordAsync(UsageSubjectKind.ProviderAccount, accountId, requests: 2, tokens: 20);

        await using var db = _db.CreateFactory().CreateDbContext();
        var rows = await db.UsageDaily.OrderBy(u => u.SubjectKind).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((UsageSubjectKind.ClientKey, 1L, 10L),
            (rows[0].SubjectKind, rows[0].Requests, rows[0].Tokens));
        Assert.Equal((UsageSubjectKind.ProviderAccount, 2L, 20L),
            (rows[1].SubjectKind, rows[1].Requests, rows[1].Tokens));
    }

    private async Task<long> SeedClientKeyAsync(long? id = null)
    {
        await using var db = _db.CreateFactory().CreateDbContext();
        var key = new ClientKey
        {
            Name = "k",
            KeyHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            KeyMask = "sk-rb-test",
        };
        if (id is long explicitId) key.Id = explicitId;
        db.ClientKeys.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }

    private async Task<long> SeedProviderAccountAsync()
    {
        await using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = $"p-{Guid.NewGuid():N}",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://p.test",
        };
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = string.Empty,
        });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Accounts[0].Id;
    }
}
