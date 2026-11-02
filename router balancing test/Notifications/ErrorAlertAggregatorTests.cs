using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Notifications;
using RouterBalancing.Core.Stats;
using System.Collections.Concurrent;
using router_balancing_test.Engine;

namespace router_balancing_test.Notifications;

/// <summary>
/// Test <see cref="ErrorAlertAggregator"/> — bộ lọc nhiễu spec §2.2.1 và cụm 30 giây
/// spec §2.2. Đồng hồ là <see cref="ManualTimeProvider"/> nên ranh giới 30s kiểm chứng
/// tức thì, không chờ thật.
/// </summary>
public class ErrorAlertAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);

    /// <summary>Bảng i18n giả — template cluster 3 placeholder để kiểm chứng thứ tự đối số.</summary>
    private static readonly Dictionary<string, string> EN = new()
    {
        ["notify.error.title"] = "t",
        ["notify.error.cluster"] = "{0}|{1}|{2}",
    };

    private static Func<string, string> Translator { get; } = key => EN[key];

    private static LogEntry Entry(
        LogSeverity severity,
        LogCategory category,
        string message,
        string? providerKey = null,
        DateTimeOffset? at = null) => new()
        {
            Severity = severity,
            Category = category,
            Message = message,
            ProviderKey = providerKey,
            Timestamp = at ?? T0,
        };

    /// <summary>Nguồn App log giả — <see cref="Raise"/> mô phỏng LogService bắn LogAdded.</summary>
    private sealed class RaisingLog : ILogService
    {
        public event Action<LogEntry>? LogAdded;

        public void Raise(LogEntry entry) => LogAdded?.Invoke(entry);

        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) { }
        public void Warn(string message, LogCategory category = LogCategory.App) { }
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) { }
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    /// <summary>Nguồn journal giả — <see cref="Raise"/> mô phỏng RequestJournalWriter bắn EntryWritten.</summary>
    private sealed class RaisingJournal : IRequestJournalWriter
    {
        public event Action<LogEntry>? EntryWritten;

        public void Raise(LogEntry entry) => EntryWritten?.Invoke(entry);
    }

    /// <summary>Aggregator + đồng hồ điều khiển được + các alert đã bắn.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Aggregator = new ErrorAlertAggregator(Log, Journal, Clock, Translator);
            // ConcurrentQueue: loop PeriodicTimer bắn alert trên thread nền trong lúc test đọc
            Aggregator.AlertRaised += Alerts.Enqueue;
        }

        public ManualTimeProvider Clock { get; } = new(T0);

        public RaisingLog Log { get; } = new();

        public RaisingJournal Journal { get; } = new();

        public ConcurrentQueue<ErrorAlert> Alerts { get; } = [];

        public ErrorAlertAggregator Aggregator { get; }

        public void Advance(int seconds) => Clock.SetUtcNow(Clock.GetUtcNow().AddSeconds(seconds));

        /// <summary>Snapshot ổn định thứ tự bắn — cho test cần index (alert thứ nhất/thứ hai).</summary>
        public List<ErrorAlert> Snapshot() => [.. Alerts];

        public void Dispose() => Aggregator.Dispose();
    }

    [Fact]
    public void Observe_AppCategoryError_EntersCluster()
    {
        using var f = new Fixture();

        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "Proxy start failed"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(1, f.Aggregator.Flush());
        var alert = Assert.Single(f.Alerts);
        Assert.Equal(1, alert.Count);
        Assert.Equal("t", alert.Title);
        // 1 lỗi duy nhất: message thô, không bọc template cluster (ErrorAlertMessage.Build)
        Assert.Equal("Proxy start failed", alert.Message);
        Assert.Equal(T0, alert.FirstAt);
        Assert.Equal(T0, alert.LastAt);
    }

    [Fact]
    public void Observe_RequestErrorWithProviderKey_EntersCluster()
    {
        using var f = new Fixture();

        // Bơm qua event EntryWritten để chứng minh ctor đã subscribe nguồn request
        f.Journal.Raise(Entry(LogSeverity.Error, LogCategory.Request, "Request failed: HTTP 502", providerKey: "openai"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(1, f.Aggregator.Flush());
        var alert = Assert.Single(f.Alerts);
        Assert.Equal(1, alert.Count);
        Assert.Equal("Request failed: HTTP 502", alert.Message);
    }

    [Fact]
    public void Observe_RequestErrorWithoutProviderKey_DoesNotAlert()
    {
        using var f = new Fixture();

        // Request chết trước khi tới upstream (body parse lỗi, model lạ, auth inbound fail)
        // → chỉ ghi log, không làm phiền user (spec §2.2.1, Review Focus #1)
        for (var i = 1; i <= 3; i++)
        {
            f.Journal.Raise(Entry(LogSeverity.Error, LogCategory.Request, $"bad payload {i}", providerKey: null));
        }
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(0, f.Aggregator.Flush());
        Assert.Empty(f.Alerts);
    }

    [Fact]
    public void Observe_WarningOrInfo_DoesNotAlert()
    {
        using var f = new Fixture();

        f.Log.Raise(Entry(LogSeverity.Warning, LogCategory.App, "Provider parked"));
        f.Aggregator.Observe(Entry(LogSeverity.Info, LogCategory.App, "Sidecar started"));
        // Warning ở Category=Request (có provider key) cũng phải rớt: ngưỡng là Error
        f.Journal.Raise(Entry(LogSeverity.Warning, LogCategory.Request, "Retry attempt 2", providerKey: "openai"));
        f.Aggregator.Observe(Entry(LogSeverity.Debug, LogCategory.App, "trace noise"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(0, f.Aggregator.Flush());
        Assert.Empty(f.Alerts);
    }

    [Fact]
    public void Flush_TenErrorsInWindow_RaisesExactlyOneAlert()
    {
        using var f = new Fixture();

        for (var i = 1; i <= 9; i++)
        {
            f.Journal.Raise(Entry(LogSeverity.Error, LogCategory.Request, $"req-{i} failed", providerKey: "openai"));
        }
        f.Log.Raise(Entry(LogSeverity.Error, LogCategory.App, "Sidecar exited"));

        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        // Review Focus #2: 10 lỗi trong một cửa sổ → ĐÚNG một alert, không phải 10
        Assert.Equal(1, f.Aggregator.Flush());
        // Cửa sổ đã đóng + buffer đã xóa → flush lần nữa phải im lặng
        Assert.Equal(0, f.Aggregator.Flush());

        var alert = Assert.Single(f.Alerts);
        Assert.Equal(10, alert.Count);
        // Message dựng qua ErrorAlertMessage với template giả "{0}|{1}|{2}" → đếm | giây | lỗi cuối
        Assert.Equal("10|30|Sidecar exited", alert.Message);
        Assert.Equal(T0, alert.FirstAt);
        Assert.Equal(T0, alert.LastAt);
    }

    [Fact]
    public void Flush_WindowBoundary_ElevenErrors_RaisesTwoAlerts()
    {
        using var f = new Fixture();

        // Cửa sổ 1: 10 lỗi. Production có loop PeriodicTimer đóng cửa sổ mỗi giây; test đóng
        // tường minh bằng Flush() để ranh giới 30s là ranh giới THẬT (clock đã advance) chứ
        // không phải cả 11 entry bị một Flush gộp lại.
        for (var i = 1; i <= 10; i++)
        {
            f.Journal.Raise(Entry(LogSeverity.Error, LogCategory.Request, $"req-{i}", providerKey: "openai", at: f.Clock.GetUtcNow()));
        }
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);
        Assert.Equal(1, f.Aggregator.Flush());

        // Cửa sổ 2: lỗi thứ 11 tới SAU ranh giới 30s → cụm mới, alert thứ hai
        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "Journal write failed", at: f.Clock.GetUtcNow()));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);
        Assert.Equal(1, f.Aggregator.Flush());

        var alerts = f.Snapshot();
        Assert.Equal(2, alerts.Count);
        Assert.Equal(10, alerts[0].Count);
        Assert.Equal("10|30|req-10", alerts[0].Message);
        Assert.Equal(1, alerts[1].Count);
        Assert.Equal("Journal write failed", alerts[1].Message);
        Assert.Equal(T0.AddSeconds(ErrorAlertAggregator.AggregationWindowSec), alerts[1].FirstAt);
    }

    [Fact]
    public void AlertRaisedSubscriberThrows_DoesNotPropagateToCaller()
    {
        using var f = new Fixture();
        var reached = new List<string>();

        // Subscriber ném ở giữa chuỗi: subscriber đứng sau vẫn phải chạy, Flush không được ném
        f.Aggregator.AlertRaised += _ => throw new InvalidOperationException("subscriber blew up");
        f.Aggregator.AlertRaised += _ => reached.Add("after-throw");

        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "boom"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);
        Assert.Equal(1, f.Aggregator.Flush());
        Assert.Contains("after-throw", reached);

        // Aggregator phải còn dùng được sau lỗi của subscriber
        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "boom again"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);
        Assert.Equal(1, f.Aggregator.Flush());
        Assert.Equal(2, f.Alerts.Count);
    }

    [Fact]
    public void Flush_BeforeWindowElapsed_KeepsClusterOpen()
    {
        using var f = new Fixture();

        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "first"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec - 1);

        Assert.Equal(0, f.Aggregator.Flush());
        Assert.Empty(f.Alerts);

        // Cửa sổ vẫn tính từ lỗi ĐẦU TIÊN, không từ lần Flush hụt phía trên
        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "second"));
        f.Advance(1);

        Assert.Equal(1, f.Aggregator.Flush());
        var alert = Assert.Single(f.Alerts);
        Assert.Equal(2, alert.Count);
        Assert.Equal("2|30|second", alert.Message);
    }

    [Fact]
    public void AlertRaisedSubscriberCallsFlushAgain_DoesNotReflushSameWindow()
    {
        using var f = new Fixture();
        var reentrantResult = -1;
        var nestedAttempts = 0;

        // Subscriber chạy đồng bộ trong lúc dispatch: bơm lỗi mới, tiến clock qua ranh giới
        // 30s rồi gọi ngược Flush(). Guard "in-flush" phải trả 0 — entry mới chờ tick kế tiếp,
        // không có alert thứ hai phát sinh trong cùng một lần dispatch (review Task 4).
        f.Aggregator.AlertRaised += _ =>
        {
            // Chặn chính subscriber đệ quy sâu: nếu guard của aggregator bị bỏ, lần dispatch
            // thứ hai chỉ cần đếm alert là đủ thấy hành vi sai (tránh StackOverflow trong test)
            if (nestedAttempts++ > 0)
            {
                return;
            }

            f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "inside handler", at: f.Clock.GetUtcNow()));
            f.Advance(ErrorAlertAggregator.AggregationWindowSec);
            reentrantResult = f.Aggregator.Flush();
        };

        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "boom"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(1, f.Aggregator.Flush());
        Assert.Equal(0, reentrantResult);
        Assert.Single(f.Alerts);
    }

    [Fact]
    public void Observe_FromManyThreads_CountsEveryEntry()
    {
        using var f = new Fixture();

        // EntryWritten bắn đồng bộ trên thread request thật → nhiều thread đẩy entry cùng lúc
        Parallel.For(1, 1001, i => f.Journal.Raise(
            Entry(LogSeverity.Error, LogCategory.Request, $"req-{i}", providerKey: "openai")));

        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(1, f.Aggregator.Flush());
        var alert = Assert.Single(f.Alerts);
        Assert.Equal(1000, alert.Count);
    }

    [Fact]
    public async Task Start_WindowElapsed_ClosesClusterWithoutManualFlush()
    {
        using var f = new Fixture();

        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "Sidecar exited"));
        f.Aggregator.Start();
        f.Aggregator.Start(); // gọi lặp không được tạo loop thứ hai

        try
        {
            // Loop PeriodicTimer tick mỗi 1 giây: chỉ cần đồng hồ (ManualTimeProvider) đã qua
            // ranh giới 30s là alert phải tới, test không tự gọi Flush(). Poll có deadline
            // (pattern LogRetentionWorkerTests.Start_ThenDispose) — không sleep cố định.
            f.Advance(ErrorAlertAggregator.AggregationWindowSec);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (f.Alerts.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            f.Aggregator.Dispose();
        }

        var alert = Assert.Single(f.Alerts);
        Assert.Equal("Sidecar exited", alert.Message);
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        using var f = new Fixture();

        f.Aggregator.Start();
        f.Aggregator.Dispose();
        f.Aggregator.Dispose();
        f.Aggregator.Dispose();
    }

    [Fact]
    public void Dispose_UnsubscribesBothSources_LateErrorDoesNotAlert()
    {
        using var f = new Fixture();
        f.Aggregator.Dispose();

        f.Log.Raise(Entry(LogSeverity.Error, LogCategory.App, "after shutdown"));
        f.Journal.Raise(Entry(LogSeverity.Error, LogCategory.Request, "after shutdown", providerKey: "openai"));
        f.Aggregator.Observe(Entry(LogSeverity.Error, LogCategory.App, "direct observe"));
        f.Advance(ErrorAlertAggregator.AggregationWindowSec);

        Assert.Equal(0, f.Aggregator.Flush());
        Assert.Empty(f.Alerts);
    }
}
