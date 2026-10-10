using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Stats;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Stats;

/// <summary>
/// RequestJournalWriter: subscribe <see cref="ITraceFeed.Published"/>, ghi 1 dòng terminal/request
/// vào LogEntries (fail-open) rồi fire <see cref="IStatsInvalidationSource.Invalidate"/> —
/// spec dashboard-stats §4 + bộ test §8.1.
/// Fixture: TraceFeed + StatsInvalidationSource thật, TestDb đã migrate, CapturingLog (pattern
/// ProxyRequestHandlerTests). Event timestamp gần "now" vì writer sweep entry &gt; 30 phút với
/// <see cref="TimeProvider.System"/>.
/// </summary>
public class RequestJournalWriterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly CapturingLog _log = new();
    private readonly TraceFeed _feed;
    private readonly StatsInvalidationSource _invalidation = new();
    private readonly RequestJournalWriter _writer;

    public RequestJournalWriterTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi writer ghi LogEntry
        DbInitializer.Initialize(_db.CreateFactory());
        _factory = _db.CreateFactory();
        _feed = new TraceFeed(_log);
        _writer = new RequestJournalWriter(_feed, _factory, _log, _invalidation, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    // Gần "now" (offset local như publish sites thật) — quá cũ sẽ bị sweep 30 phút evict
    private static DateTimeOffset Now => DateTimeOffset.Now;

    private static TraceEvent Ev(string id, TraceStage stage, string model, DateTimeOffset at,
        TraceRoute? route = null, int? status = null, bool? success = null, string? mode = null,
        long? clientKeyId = null) =>
        new(id, stage, model, route, null, status, success, null, at, mode, ClientKeyId: clientKeyId);

    private List<LogEntry> Rows()
    {
        using var db = _factory.CreateDbContext();
        return db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToList();
    }

    [Fact]
    public void Finished_Success_WritesOkRowWithRouteAndDuration()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0, clientKeyId: 7));
        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", t0.AddSeconds(1),
            route: new TraceRoute(null, "prov1", null)));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(1.5),
            status: 200, success: true));

        var row = Assert.Single(Rows());
        Assert.Equal(RequestOutcome.Ok, row.Outcome);
        Assert.Equal("prov1", row.ProviderKey);
        Assert.Equal("m1", row.ModelKey);
        Assert.Equal(200, row.HttpStatus);
        Assert.Equal(1500, row.DurationMs);
        Assert.Equal(7, row.ClientKeyId);
        // Publish sites dùng offset local — writer phải chuẩn hóa UTC (offset 0)
        Assert.Equal(t0.AddSeconds(1.5).ToUniversalTime(), row.Timestamp);
        Assert.Equal(TimeSpan.Zero, row.Timestamp.Offset);
    }

    [Fact]
    public void Finished_Failure_WritesErrorRowWithDerivedFailureKind()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(1), status: 503, success: false));
        _feed.Publish(Ev("r2", TraceStage.Received, "m2", t0));
        _feed.Publish(Ev("r2", TraceStage.Finished, "m2", t0.AddSeconds(2), status: null, success: false));

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        var http = rows.Single(r => r.RequestId == "r1");
        Assert.Equal(RequestOutcome.Error, http.Outcome);
        Assert.Equal("http", http.ErrorCode);
        Assert.Equal(LogSeverity.Error, http.Severity);
        Assert.Equal(503, http.HttpStatus);
        var network = rows.Single(r => r.RequestId == "r2");
        Assert.Equal(RequestOutcome.Error, network.Outcome);
        Assert.Equal("network", network.ErrorCode);
        Assert.Null(network.HttpStatus);
    }

    [Fact]
    public void Canceled_WritesCancelledRow()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Canceled, "m1", t0.AddSeconds(2)));

        var row = Assert.Single(Rows());
        Assert.Equal(RequestOutcome.Cancelled, row.Outcome);
        Assert.Equal(LogSeverity.Info, row.Severity);
        Assert.Equal(2000, row.DurationMs);
        Assert.Null(row.HttpStatus);
        Assert.Null(row.ErrorCode);
    }

    [Fact]
    public void ReceivedPublishedTwice_KeepsOriginalStartedAt()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        // G2 HeadersSent republish Received — không được đè StartedAt của entry gốc
        _feed.Publish(new TraceEvent("r1", TraceStage.Received, "m1", null, null, null, null, null,
            t0.AddSeconds(5), HeadersSent: true));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(2), status: 200, success: true));

        var row = Assert.Single(Rows());
        // 2000 tính từ StartedAt gốc — không phải −3000 (bị đè bằng t0+5s) hay 5000 (lệch mốc)
        Assert.Equal(2000, row.DurationMs);
    }

    [Fact]
    public void MultipleAttempts_LatestAttemptWinsRoute()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", t0.AddSeconds(1),
            route: new TraceRoute(null, "p1", null)));
        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", t0.AddSeconds(2),
            route: new TraceRoute(null, "p2", null)));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(3), status: 200, success: true));

        var row = Assert.Single(Rows());
        Assert.Equal("p2", row.ProviderKey);
    }

    [Fact]
    public void DbFailure_LogsAndDoesNotInvalidate()
    {
        var log = new CapturingLog();
        var feed = new TraceFeed(log);
        var invalidation = new StatsInvalidationSource();
        var changed = 0;
        invalidation.Changed += () => changed++;
        _ = new RequestJournalWriter(feed, new ThrowingFactory(), log, invalidation, TimeProvider.System);
        // Subscribe SAU writer: delegate multicast dừng ở subscriber nổ — nếu handler
        // journal nổ thì subscriber phía sau mất event (bài học RequestTrace)
        var downstream = 0;
        feed.Published += _ => downstream++;

        var ex = Record.Exception(() =>
        {
            feed.Publish(Ev("r1", TraceStage.Received, "m1", Now));
            feed.Publish(Ev("r1", TraceStage.Finished, "m1", Now.AddSeconds(1), status: 200, success: true));
        });

        Assert.Null(ex);                    // không nổ feed
        Assert.Equal(2, downstream);        // subscriber phía sau vẫn nhận đủ 2 event
        Assert.Equal(0, changed);           // ghi thất bại → không invalidate
        Assert.Contains(log.Errors, m => m.Contains("RequestJournalWriter"));
    }

    [Fact]
    public void Write_Success_InvalidatesSource()
    {
        var changed = 0;
        _invalidation.Changed += () => changed++;
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", t0.AddSeconds(1),
            route: new TraceRoute(null, "prov1", null)));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(1.5), status: 200, success: true));

        Assert.Equal(1, changed);   // chỉ terminal ghi dòng mới fire — 1 lần
    }

    [Fact]
    public void PriorityUpdateBeforeReceived_MergesClientKeyIdKeepingStartedAt()
    {
        var t0 = Now;
        // Priority-update (G3, RequestQueue.PublishPriorityUpdate) đến TRƯỚC Received thật
        // trong cửa sổ microgiây Enqueue→Received — ClientKeyId chưa auth xong nên null
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0.AddMilliseconds(50), clientKeyId: 7));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(1.5), status: 200, success: true));

        var row = Assert.Single(Rows());
        Assert.Equal(7, row.ClientKeyId);       // merge từ Received thật
        Assert.Equal(1500, row.DurationMs);     // StartedAt gốc t0 — không reset khi merge
    }

    [Fact]
    public void PriorityUpdateThenReceived_WritesRowWithClientKeyId()
    {
        var t0 = Now;
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0.AddMilliseconds(50), clientKeyId: 7));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddSeconds(1), status: 200, success: true));

        Assert.Equal(7, Assert.Single(Rows()).ClientKeyId);
    }

    [Fact]
    public void StaleEntry_OlderThan30Minutes_IsEvicted()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-40);
        _feed.Publish(Ev("r1", TraceStage.Received, "m1", t0));
        // Event thứ 2 cách t0 31 phút → sweep chạy trước khi xử lý, xóa entry cũ
        _feed.Publish(Ev("r1", TraceStage.Attempt, "m1", t0.AddMinutes(31),
            route: new TraceRoute(null, "p1", null)));
        _feed.Publish(Ev("r1", TraceStage.Finished, "m1", t0.AddMinutes(32), status: 200, success: true));

        Assert.Empty(Rows());
    }

    /// <summary>Factory nổ khi tạo DbContext — mô phỏng DB không dùng được (test fail-open).</summary>
    private sealed class ThrowingFactory : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() =>
            throw new InvalidOperationException("db offline");
    }

    /// <summary>ILogService ghi vào list để test assert log Error (pattern ProxyRequestHandlerTests).</summary>
    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public List<LogEntry> Entries { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }

        public void Write(LogEntry entry) => Route(entry);

        public void Info(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });

        public void Warn(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });

        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });

        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens)
        {
        }

        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;

        private void Route(LogEntry entry)
        {
            Entries.Add(entry);
            switch (entry.Severity)
            {
                case LogSeverity.Error: Errors.Add(entry.Message); break;
                case LogSeverity.Warning: Warns.Add(entry.Message); break;
                default: Infos.Add(entry.Message); break;
            }
        }
    }
}
