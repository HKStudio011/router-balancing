using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Logging;

public class LogServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public LogServiceTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi ghi LogEntry
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private LogService Create() => new(_db.CreateFactory());

    [Fact]
    public void Write_ThenQuery_ReturnsEntry()
    {
        using var service = Create();

        service.Info("app started");

        var result = service.Query(new LogQuery());
        var entry = Assert.Single(result);
        Assert.Equal("app started", entry.Message);
        Assert.Equal(LogSeverity.Info, entry.Severity);
        Assert.Equal(LogCategory.App, entry.Category);
    }

    [Fact]
    public void Write_FiresLogAdded()
    {
        using var service = Create();
        LogEntry? notified = null;
        service.LogAdded += e => notified = e;

        service.Warn("port busy");

        Assert.NotNull(notified);
        Assert.Equal("port busy", notified.Message);
    }

    [Fact]
    public void Write_WhenOneSubscriberThrows_OthersStillNotifiedAndErrorLogged()
    {
        using var service = Create();
        var throwingCalls = 0;
        var recorded = new List<LogEntry>();
        // Handler ném lỗi đăng ký TRƯỚC: multicast delegate mặc định dừng ở đây,
        // nên handler ghi nhận phía sau sẽ mất event nếu không được cách ly.
        service.LogAdded += _ =>
        {
            throwingCalls++;
            throw new InvalidOperationException("UI thread only");
        };
        service.LogAdded += recorded.Add;

        service.Info("user action");

        Assert.Equal(1, throwingCalls);
        var notified = Assert.Single(recorded);
        Assert.Equal("user action", notified.Message);

        // Lỗi handler phải nằm trong store, và KHÔNG được phát lại LogAdded
        // (nếu phát lại thì recorded sẽ có 2 phần tử / đệ quy vô hạn).
        using var verify = Create();
        var error = Assert.Single(verify.Query(new LogQuery(MinSeverity: LogSeverity.Error)));
        Assert.Contains("LogAdded", error.Message);
        Assert.Contains("user action", error.Message);
        Assert.Contains("UI thread only", error.Details);
        Assert.Equal(nameof(InvalidOperationException), error.ErrorCode);
        Assert.Equal(LogCategory.App, error.Category);
    }

    [Fact]
    public void Query_WhenMinSeverityWarning_ExcludesInfo()
    {
        using var service = Create();
        service.Info("info line");
        service.Warn("warn line");
        service.Error("error line");

        var result = service.Query(new LogQuery(MinSeverity: LogSeverity.Warning));

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Severity == LogSeverity.Info);
    }

    [Fact]
    public void Query_WhenSearchMatches_ReturnsOnlyMatch()
    {
        using var service = Create();
        service.Info("startup complete");
        service.Info("provider timeout");

        var result = service.Query(new LogQuery(Search: "timeout"));

        var entry = Assert.Single(result);
        Assert.Equal("provider timeout", entry.Message);
    }

    [Fact]
    public void Query_WhenCategoryFilterApplied_ReturnsOnlyThatCategory()
    {
        using var service = Create();
        service.Info("app line");
        service.Write(new LogEntry { Message = "request line", Category = LogCategory.Request });

        var result = service.Query(new LogQuery(Category: LogCategory.Request));

        var entry = Assert.Single(result);
        Assert.Equal("request line", entry.Message);
    }

    [Fact]
    public void Query_PaginatesByPageAndPageSize()
    {
        using var service = Create();
        for (var i = 0; i < 5; i++) service.Info($"line {i}");

        var page2 = service.Query(new LogQuery(Page: 2, PageSize: 2));

        Assert.Equal(2, page2.Count);
        Assert.Equal(5, service.Count(new LogQuery()));
    }

    [Fact]
    public void Query_WhenFromToSet_ReturnsOnlyInRange()
    {
        using var service = Create();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(2);
        service.Write(new LogEntry { Message = "before", Timestamp = from.AddHours(-1) });
        service.Write(new LogEntry { Message = "at from", Timestamp = from });
        service.Write(new LogEntry { Message = "middle", Timestamp = from.AddHours(1) });
        service.Write(new LogEntry { Message = "at to", Timestamp = to });
        service.Write(new LogEntry { Message = "after", Timestamp = to.AddHours(1) });

        var query = new LogQuery(From: from, To: to);

        var result = service.Query(query);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "at to", "middle", "at from" }, result.Select(e => e.Message));
        Assert.Equal(3, service.Count(query));
    }

    [Fact]
    public void Query_EntriesPersistAcrossServiceInstances()
    {
        using (var service = Create())
        {
            service.Error("boom", new InvalidOperationException("detail"));
        }

        using var reopened = Create();
        var entry = Assert.Single(reopened.Query(new LogQuery()));
        Assert.Equal(LogSeverity.Error, entry.Severity);
        Assert.Contains("detail", entry.Details);
    }

    [Fact]
    public void LogRequestUsage_PersistsRowWithTokensAndIds()
    {
        var service = Create();

        service.LogRequestUsage("req-1", 42, 10, 5);

        var entry = Assert.Single(service.Query(new LogQuery()));
        Assert.Equal(LogCategory.Request, entry.Category);
        Assert.Equal(LogSeverity.Info, entry.Severity);
        Assert.Equal(10, entry.PromptTokens);
        Assert.Equal(5, entry.CompletionTokens);
        Assert.Equal("req-1", entry.RequestId);
        Assert.Equal(42, entry.ClientKeyId);
    }
}
