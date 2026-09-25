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
}
