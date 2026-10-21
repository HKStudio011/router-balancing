using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Usage;

namespace router_balancing_test.Server;

public class ClientKeyServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly TestDb _db = new();
    private readonly ClientKeyService _service;

    public ClientKeyServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var usage = new UsageDailyService(_db.CreateFactory(), new FixedTimeProvider(Now));
        _service = new ClientKeyService(_db.CreateFactory(), usage);
    }

    public void Dispose() => _db.Dispose();

    private static ClientKeyDraft Draft(string name = "app", int? rpm = null, int? tpm = null)
        => new(name, rpm, tpm);

    [Fact]
    public async Task Create_ReturnsPlaintextAndPersistsOnlyHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        Assert.StartsWith("sk-rb-", plaintext);
        await using var db = _db.CreateFactory().CreateDbContext();
        var saved = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
        Assert.Equal(ClientKeyHasher.Mask(plaintext), saved.KeyMask);
        Assert.DoesNotContain(plaintext, saved.KeyHash + saved.KeyMask + saved.Name);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task Create_TriggersKeysChanged()
    {
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.CreateAsync(Draft());

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Create_WhenNameBlank_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("  ")));

    [Fact]
    public async Task Create_WhenNameOver100Chars_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft(new string('x', 101))));

    [Fact]
    public async Task Create_WhenLimitNotPositive_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", rpm: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", tpm: -1)));
    }

    [Fact]
    public async Task List_ReturnsAllOrderedById()
    {
        await _service.CreateAsync(Draft("first"));
        await _service.CreateAsync(Draft("second"));

        var list = await _service.ListAsync();

        Assert.Equal(new[] { "first", "second" }, list.Select(k => k.Name).ToArray());
    }

    [Fact]
    public async Task Update_ChangesNameAndLimits_KeepsHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        await _service.UpdateAsync(key.Id, Draft("renamed", rpm: 60, tpm: 1000));

        var updated = await _service.ListAsync();
        var saved = Assert.Single(updated);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal(60, saved.RatePerMinute);
        Assert.Equal(1000, saved.TokensPerMinute);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
    }

    [Fact]
    public async Task Update_WhenMissing_Throws()
        => await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateAsync(999, Draft()));

    [Fact]
    public async Task Delete_RemovesRowAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.DeleteAsync(key.Id);

        Assert.Empty(await _service.ListAsync());
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SetEnabled_TogglesAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.SetEnabledAsync(key.Id, false);

        Assert.False((await _service.ListAsync()).Single().Enabled);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RecordRequest_IncrementsTodayUsageAndLastUsed()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
        Assert.NotNull(saved.LastUsedAt);

        await using var db = _db.CreateFactory().CreateDbContext();
        var row = await db.UsageDaily.SingleAsync(
            u => u.SubjectKind == UsageSubjectKind.ClientKey && u.SubjectId == key.Id);
        Assert.Equal(Today, row.Date);
        Assert.Equal(1, row.Requests);
        Assert.Equal(0, row.Tokens);
    }

    [Fact]
    public async Task RecordRequest_KeepsYesterdayUsageAndStartsTodayRow()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var yesterdayUsage = new UsageDailyService(
            _db.CreateFactory(), new FixedTimeProvider(Now.AddDays(-1)));
        await yesterdayUsage.RecordAsync(UsageSubjectKind.ClientKey, key.Id, requests: 50, tokens: 100);

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);   // chỉ usage hôm nay, không cộng dồn ngày cũ
        Assert.Equal(0, saved.TokensUsed);

        await using var db = _db.CreateFactory().CreateDbContext();
        var rows = await db.UsageDaily
            .Where(u => u.SubjectKind == UsageSubjectKind.ClientKey && u.SubjectId == key.Id)
            .OrderBy(u => u.Date)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-1).UtcDateTime), rows[0].Date);
        Assert.Equal((50L, 100L), (rows[0].Requests, rows[0].Tokens));
        Assert.Equal(Today, rows[1].Date);
        Assert.Equal((1L, 0L), (rows[1].Requests, rows[1].Tokens));
    }

    [Fact]
    public async Task RecordTokens_AddsPromptPlusCompletion()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordTokensAsync(key.Id, 12, 34);

        Assert.Equal(46, (await _service.ListAsync()).Single().TokensUsed);
    }

    [Fact]
    public async Task RecordTokens_KeepsYesterdayUsageAndAddsToday()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var yesterdayUsage = new UsageDailyService(
            _db.CreateFactory(), new FixedTimeProvider(Now.AddDays(-1)));
        await yesterdayUsage.RecordAsync(UsageSubjectKind.ClientKey, key.Id, requests: 50, tokens: 100);

        await _service.RecordTokensAsync(key.Id, 3, 4);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(7, saved.TokensUsed);     // chỉ usage hôm nay
        Assert.Equal(0, saved.RequestsUsed);

        await using var db = _db.CreateFactory().CreateDbContext();
        var rows = await db.UsageDaily
            .Where(u => u.SubjectKind == UsageSubjectKind.ClientKey && u.SubjectId == key.Id)
            .OrderBy(u => u.Date)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((50L, 100L), (rows[0].Requests, rows[0].Tokens));
        Assert.Equal((0L, 7L), (rows[1].Requests, rows[1].Tokens));
    }

    [Fact]
    public async Task RecordRequest_WhenKeyDeleted_NoThrowAndNoOrphanUsage()
    {
        await _service.RecordRequestAsync(999); // fail-open: thống kê không được nổ

        await using var db = _db.CreateFactory().CreateDbContext();
        Assert.Empty(await db.UsageDaily.ToListAsync());
    }
}
