using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Stats;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Stats;

/// <summary>
/// StatsService: đọc journal từ SQLite (2 query: dòng terminal + dòng usage) rồi aggregate
/// LINQ-to-objects theo cửa sổ UTC — spec dashboard-stats §5 + bộ test §8.2.
/// Fixture: TestDb đã migrate, seed LogEntry trực tiếp, <see cref="FixedTimeProvider"/>
/// đóng băng "now" để test boundary nửa đêm UTC không phụ thuộc giờ hệ thống.
/// </summary>
public class StatsServiceTests : IDisposable
{
    // "Now" cố định 2026-10-11 15:30 UTC — nửa đêm UTC cùng ngày = 2026-10-11 00:00 UTC
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MidnightUtc = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly StatsService _service;

    public StatsServiceTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi seed LogEntry
        DbInitializer.Initialize(_db.CreateFactory());
        _factory = _db.CreateFactory();
        _service = new StatsService(_factory, new FixedTimeProvider(Now));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ErrorRate_ExcludesCancelled()
    {
        var at = MidnightUtc.AddHours(9);
        var entries = new List<LogEntry>();
        entries.AddRange(Enumerable.Range(0, 8)
            .Select(_ => Terminal(RequestOutcome.Ok, at, durationMs: 100)));
        entries.Add(Terminal(RequestOutcome.Error, at, durationMs: 100));
        entries.AddRange(Enumerable.Range(0, 3)
            .Select(_ => Terminal(RequestOutcome.Cancelled, at, durationMs: 100)));
        Seed([.. entries]);

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal(12, snapshot.Requests);
        Assert.Equal(8, snapshot.Ok);
        Assert.Equal(1, snapshot.Errors);
        Assert.Equal(3, snapshot.Cancelled);
        // Errors / (Ok+Errors) = 1/9 ≈ 11.11% — Cancelled là chủ đích client, không vào
        // denominator; nếu lọt vào sẽ ra 1/12 ≈ 8.33% (spec §5, Review Focus #3)
        Assert.Equal(100.0 / 9, snapshot.ErrorRatePercent, 10);
    }

    [Fact]
    public async Task P95_NearestRank()
    {
        // 20 Ok, DurationMs 1..20 — nearest-rank: ceil(0.95×20)=19 → index 18 khi sort ASC = 19
        var at = MidnightUtc.AddHours(8);
        Seed([.. Enumerable.Range(1, 20)
            .Select(ms => Terminal(RequestOutcome.Ok, at, durationMs: ms))]);

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal(20, snapshot.Ok);
        Assert.Equal(19, snapshot.P95LatencyMs);
    }

    [Fact]
    public async Task TodayWindow_UsesUtcMidnight()
    {
        Seed(
            Terminal(RequestOutcome.Ok, MidnightUtc.AddMilliseconds(-1)), // 1ms trước nửa đêm UTC → ngoài
            Terminal(RequestOutcome.Ok, MidnightUtc),                     // đúng nửa đêm → trong
            Terminal(RequestOutcome.Ok, Now.AddHours(1)));                 // tương lai → ngoài

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal(MidnightUtc, snapshot.From);
        Assert.Equal(Now, snapshot.To);
        Assert.Equal(TimeSpan.Zero, snapshot.From.Offset);
        Assert.Equal(1, snapshot.Requests);
        Assert.Equal(1, snapshot.Ok);
    }

    [Fact]
    public async Task Breakdown_JoinsUsageTokensByRequestId()
    {
        var at = MidnightUtc.AddHours(10);
        SeedClientKey(7, "app-7");
        Seed(
            Terminal(RequestOutcome.Ok, at, durationMs: 50, providerKey: "p1", modelKey: "m1",
                clientKeyId: 7, requestId: "r1"),
            Usage("r1", promptTokens: 100, completionTokens: 20, at),
            Usage("r1", promptTokens: 50, completionTokens: 10, at),
            // Usage không có terminal tương ứng — vào tổng snapshot nhưng không vào breakdown
            Usage("r2", promptTokens: 7, completionTokens: 3, at));

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        var provider = Assert.Single(snapshot.ByProvider);
        Assert.Equal("p1", provider.Key);
        Assert.Equal(1, provider.Requests);  // requests đếm từ dòng terminal, không từ usage
        Assert.Equal(0, provider.Errors);
        Assert.Equal(180L, provider.Tokens); // 150 prompt + 30 completion của r1
        Assert.Equal(180L, Assert.Single(snapshot.ByModel).Tokens);
        // Breakdown client key hiển thị TÊN (Name), không phải id
        var clientKey = Assert.Single(snapshot.ByClientKey);
        Assert.Equal("app-7", clientKey.Key);
        Assert.Equal(180L, clientKey.Tokens);
        // Tổng snapshot = mọi dòng usage trong cửa sổ (kể cả r2 không join được terminal)
        Assert.Equal(157, snapshot.PromptTokens);
        Assert.Equal(33, snapshot.CompletionTokens);
    }

    [Fact]
    public async Task Breakdown_ClientKey_DeletedKeyFallsBackToId()
    {
        // Key đã bị xóa khỏi bảng ClientKeys nhưng journal còn ClientKeyId mồ côi
        Seed(Terminal(RequestOutcome.Ok, MidnightUtc.AddHours(5), clientKeyId: 99));

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal("99", Assert.Single(snapshot.ByClientKey).Key);
    }

    [Fact]
    public async Task Buckets_JoinUsageTokensByRequestId()
    {
        var hour3 = MidnightUtc.AddHours(3);
        var hour10 = MidnightUtc.AddHours(10);
        Seed(
            Terminal(RequestOutcome.Ok, hour3, durationMs: 50, requestId: "r1"),
            Usage("r1", promptTokens: 100, completionTokens: 20, hour3),
            Usage("r1", promptTokens: 50, completionTokens: 10, hour3),
            Terminal(RequestOutcome.Ok, hour10, durationMs: 80, requestId: "r2"),
            Usage("r2", promptTokens: 7, completionTokens: 3, hour10),
            // Usage không có terminal — vào tổng snapshot nhưng không vào bucket
            Usage("r3", promptTokens: 999, completionTokens: 111, hour3));

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        // Bucket theo Timestamp của dòng terminal; token join theo RequestId (cùng rule breakdown)
        Assert.Equal(150L, snapshot.Buckets[3].PromptTokens);
        Assert.Equal(30L, snapshot.Buckets[3].CompletionTokens);
        Assert.Equal(7L, snapshot.Buckets[10].PromptTokens);
        Assert.Equal(3L, snapshot.Buckets[10].CompletionTokens);
        Assert.All(snapshot.Buckets.Where(b => b.BucketStart != hour3 && b.BucketStart != hour10), b =>
        {
            Assert.Equal(0L, b.PromptTokens);
            Assert.Equal(0L, b.CompletionTokens);
        });
        // Tổng snapshot vẫn cộng mọi usage trong cửa sổ (r3 không join được terminal)
        Assert.Equal(1156, snapshot.PromptTokens);
        Assert.Equal(144, snapshot.CompletionTokens);
    }

    [Fact]
    public async Task EmptyWindow_ReturnsZerosAndNullLatency()
    {
        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal(0, snapshot.Requests);
        Assert.Equal(0, snapshot.Ok);
        Assert.Equal(0, snapshot.Errors);
        Assert.Equal(0, snapshot.Cancelled);
        Assert.Equal(0.0, snapshot.ErrorRatePercent);
        Assert.Null(snapshot.AvgLatencyMs);
        Assert.Null(snapshot.P95LatencyMs);
        Assert.Equal(0, snapshot.PromptTokens);
        Assert.Equal(0, snapshot.CompletionTokens);
        Assert.Equal(24, snapshot.Buckets.Count);
        Assert.All(snapshot.Buckets, b =>
        {
            Assert.Equal(0, b.Requests);
            Assert.Equal(0, b.Errors);
            Assert.Equal(0L, b.PromptTokens);
            Assert.Equal(0L, b.CompletionTokens);
        });
        Assert.Empty(snapshot.ByProvider);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByClientKey);
    }

    [Fact]
    public async Task TodayBuckets_Hourly24()
    {
        Seed(
            Terminal(RequestOutcome.Ok, MidnightUtc.AddHours(3).AddMinutes(30)),
            Terminal(RequestOutcome.Error, MidnightUtc.AddHours(3).AddMinutes(45)),
            Terminal(RequestOutcome.Ok, MidnightUtc.AddHours(10).AddMinutes(15)),
            Terminal(RequestOutcome.Cancelled, MidnightUtc.AddHours(14)));

        var snapshot = await _service.GetRangeAsync(StatsRange.Today);

        Assert.Equal(24, snapshot.Buckets.Count);
        for (var h = 0; h < 24; h++)
            Assert.Equal(MidnightUtc.AddHours(h), snapshot.Buckets[h].BucketStart);
        Assert.Equal(TimeSpan.Zero, snapshot.Buckets[0].BucketStart.Offset);
        // 2 dòng ở 03h — lỗi nằm đúng bucket theo Timestamp
        Assert.Equal(2, snapshot.Buckets[3].Requests);
        Assert.Equal(1, snapshot.Buckets[3].Errors);
        Assert.Equal(1, snapshot.Buckets[10].Requests);
        Assert.Equal(0, snapshot.Buckets[10].Errors);
        // Cancelled vẫn tính vào Requests của bucket, không tính vào Errors
        Assert.Equal(1, snapshot.Buckets[14].Requests);
        Assert.Equal(0, snapshot.Buckets[14].Errors);
        Assert.Equal(0, snapshot.Buckets[0].Requests);
        Assert.Equal(0, snapshot.Buckets[23].Requests);
    }

    private void Seed(params LogEntry[] entries)
    {
        using var db = _factory.CreateDbContext();
        db.LogEntries.AddRange(entries);
        db.SaveChanges();
    }

    private void SeedClientKey(long id, string name)
    {
        using var db = _factory.CreateDbContext();
        db.ClientKeys.Add(new ClientKey
        {
            Id = id,
            Name = name,
            KeyHash = new string('0', 64),
            KeyMask = "sk-rb-…test",
        });
        db.SaveChanges();
    }

    private static LogEntry Terminal(RequestOutcome outcome, DateTimeOffset at, int? durationMs = null,
        string? providerKey = null, string? modelKey = null, long? clientKeyId = null,
        string? requestId = null) =>
        new()
        {
            Timestamp = at,
            Severity = outcome == RequestOutcome.Error ? LogSeverity.Error : LogSeverity.Info,
            Category = LogCategory.Request,
            Message = "terminal",
            Outcome = outcome,
            DurationMs = durationMs,
            ProviderKey = providerKey,
            ModelKey = modelKey,
            ClientKeyId = clientKeyId,
            RequestId = requestId,
        };

    private static LogEntry Usage(string requestId, int promptTokens, int completionTokens,
        DateTimeOffset at) =>
        new()
        {
            Timestamp = at,
            Severity = LogSeverity.Info,
            Category = LogCategory.Request,
            Message = "usage",
            RequestId = requestId,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
        };
}
