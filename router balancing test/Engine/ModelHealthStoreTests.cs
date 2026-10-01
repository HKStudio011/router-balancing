using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ModelHealthStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly CapturingLog _log = new();
    private readonly FakeTime _time = new();

    public ModelHealthStoreTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    private ModelHealthStore CreateSut() => new(_settings, _log, _time);

    // Mở fuse bằng đúng MaxRetry lần RecordFailure — retryAfter (nếu có) chỉ gửi ở lần
    // cuối cùng (đúng transition: floor lịch probe khi MỞ fuse, không gửi ở các lần dưới ngưỡng)
    private void OpenFuse(ModelHealthStore sut, string modelId, TimeSpan? retryAfter = null)
    {
        var max = _settings.MaxRetry;
        for (var i = 1; i <= max; i++)
            sut.RecordFailure(modelId, i == max ? retryAfter : null);
    }

    [Fact]
    public void RecordFailure_UnknownModel_KeepsHealthy()
    {
        var sut = CreateSut();

        sut.RecordFailure("m1");

        // Dưới ngưỡng MaxRetry=3 → chưa mở fuse, cũng chưa hiện trong list ManualRetry
        Assert.False(sut.IsManualRetry("m1"));
        Assert.Empty(sut.GetManualRetryModels());
    }

    [Fact]
    public void RecordFailure_BelowMaxRetry_StaysHealthyAndSilent()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        Assert.False(sut.IsManualRetry("m1"));
        // Chưa transition → không log Warn nào (chỉ 4 event §5 được ghi)
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public void RecordFailure_AtMaxRetry_OpensFuseWithWarnAndDueNow()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        Assert.True(sut.IsManualRetry("m1"));
        var model = Assert.Single(sut.GetManualRetryModels());
        Assert.Equal("m1", model.ModelId);
        // Vừa mở fuse, chưa probe lần nào — lịch probe = ngay bây giờ
        Assert.Equal(0, model.AttemptsMade);
        Assert.Equal(_time.GetUtcNow(), model.NextProbeAt);
        var warn = Assert.Single(_log.Warns);
        Assert.Contains("chuyển sang ManualRetry", warn);
        Assert.Contains("3 lỗi liên tiếp", warn);
    }

    [Fact]
    public void RecordFailure_WhileFuseOpen_DoesNotLogWarnTwice()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");
        sut.RecordFailure("m1"); // lỗi thêm khi fuse đã mở — KHÔNG log Warn lần 2

        Assert.True(sut.IsManualRetry("m1"));
        Assert.Single(_log.Warns);
    }

    [Fact]
    public void RecordSuccess_WhileManualRetry_LogsRecoverInfoAndClearsState()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        sut.RecordSuccess("m1");

        Assert.False(sut.IsManualRetry("m1"));
        Assert.Empty(sut.GetManualRetryModels());
        var info = Assert.Single(_log.Infos);
        Assert.Contains("phục hồi", info);

        // Entry đã xóa hoàn toàn — lần lỗi kế bắt đầu từ 0, không phải "vừa recover"
        sut.RecordFailure("m1");
        Assert.False(sut.IsManualRetry("m1"));
        Assert.Single(_log.Infos); // không log recover lần 2
    }

    [Fact]
    public void RecordSuccess_BelowMaxRetry_ResetsCounterSilently()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        sut.RecordSuccess("m1");

        // Chưa từng mở fuse → không có Info recover; không có Warn nào
        Assert.Empty(_log.Infos);
        Assert.Empty(_log.Warns);
        // Counter đã reset — 2 lần lỗi nữa (tổng kể cả trước success = 4 nếu không reset)
        // vẫn dưới ngưỡng 3 của chuỗi liên tiếp mới
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");
        Assert.False(sut.IsManualRetry("m1"));
    }

    [Fact]
    public void RecordFailure_WithRetryAfter_FloorsNextProbeAtWhenFuseOpens()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        var sut = CreateSut();

        sut.RecordFailure("m1", TimeSpan.FromSeconds(300));

        var model = Assert.Single(sut.GetManualRetryModels());
        // Floor Retry-After: lịch probe = now + 300s chứ không phải now (spec §3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300), model.NextProbeAt);
    }

    [Fact]
    public void RecordProbeFailure_BackoffDoublesEachAttempt()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        var first = sut.RecordProbeFailure("m1");
        Assert.Equal(1, first.AttemptsMade);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60), first.NextProbeAt);

        var second = sut.RecordProbeFailure("m1");
        Assert.Equal(2, second.AttemptsMade);
        Assert.Equal(_time.GetUtcNow().AddSeconds(120), second.NextProbeAt);

        // 3 Warn = 1 mở fuse (từ OpenFuse) + 2 probe fail — mỗi lần 1 dòng, không lặp
        Assert.Equal(3, _log.Warns.Count);
        Assert.Contains(_log.Warns,
            w => w.Contains("Probe model 'm1'") && w.Contains("lần 1/3") && w.Contains("60s"));
        Assert.Contains(_log.Warns, w => w.Contains("lần 2/3") && w.Contains("120s"));
    }

    [Fact]
    public void RecordProbeFailure_RetryAfterLargerThanBackoff_FloorsToRetryAfter()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        var result = sut.RecordProbeFailure("m1", TimeSpan.FromSeconds(300));

        Assert.Equal(1, result.AttemptsMade);
        // max(60s×1, 300s) = 300s — Retry-After thắng backoff (spec §3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300), result.NextProbeAt);
    }

    [Fact]
    public void RecordProbeFailure_AtMaxRetry_ReturnsNullAndLogsExhausted()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        var sut = CreateSut();
        sut.RecordFailure("m1"); // MaxRetry=1 → mở fuse ngay lần đầu

        var result = sut.RecordProbeFailure("m1");

        Assert.Equal(1, result.AttemptsMade);
        Assert.Null(result.NextProbeAt); // hết lượt — watchdog không probe nữa
        Assert.Contains(_log.Warns, w => w.Contains("hết lượt probe tự động"));
    }

    [Fact]
    public void RecordFailure_Parallel400Times_LogsFuseOpenExactlyOnce()
    {
        var sut = CreateSut();

        Parallel.For(0, 400, _ => sut.RecordFailure("m1"));

        Assert.True(sut.IsManualRetry("m1"));
        // Transition log dưới lock check-then-act: đúng 1 thread thấy Healthy→ManualRetry
        Assert.Equal(1, _log.Warns.Count(w => w.Contains("chuyển sang ManualRetry")));
    }

    [Fact]
    public void RecordProbeFailure_UnknownModel_ReturnsZeroAndKeepsHealthy()
    {
        var sut = CreateSut();

        var result = sut.RecordProbeFailure("ghost");

        Assert.Equal(0, result.AttemptsMade);
        Assert.Null(result.NextProbeAt);
        Assert.False(sut.IsManualRetry("ghost"));
        Assert.Empty(_log.Warns);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
        public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Errors.Add(message);
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
