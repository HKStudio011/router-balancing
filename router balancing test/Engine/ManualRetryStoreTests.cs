using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;

namespace router_balancing_test.Engine;

public class ManualRetryStoreTests
{
    private readonly CapturingLog _log = new();
    private readonly FakeTime _time = new();

    private ManualRetryStore CreateSut() => new(_log, _time);

    [Fact]
    public void Park_WhenNewProviderEntry_ParksWarnsOnceAndFiresChanged()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => changed++;

        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);

        Assert.True(sut.IsProviderParked(1));
        var entry = Assert.Single(sut.GetEntries());
        Assert.Equal(ManualRetryLevel.Provider, entry.Level);
        Assert.Equal(1, entry.Id);
        Assert.Equal("", entry.ModelId);
        Assert.Equal(ManualRetryReason.Unreachable, entry.Reason);
        Assert.Equal(_time.GetUtcNow(), entry.ParkedAt);
        var warn = Assert.Single(_log.Warns);
        Assert.Contains("Đưa provider '1' vào danh sách retry thủ công: Unreachable", warn);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Park_WhenAccountEntry_ModelIdNormalizedToEmpty()
    {
        var sut = CreateSut();

        sut.Park(ManualRetryLevel.Account, 7, "ignored", ManualRetryReason.Unauthorized);

        var entry = Assert.Single(sut.GetEntries());
        Assert.Equal("", entry.ModelId); // V4: account không mang ModelId
        Assert.True(sut.IsAccountParked(7));
    }

    [Fact]
    public void Park_WhenModelEntry_IdNormalizedToZero()
    {
        var sut = CreateSut();

        sut.Park(ManualRetryLevel.Model, 42, "gpt-x", ManualRetryReason.ModelNotFound);

        var entry = Assert.Single(sut.GetEntries());
        Assert.Equal(0, entry.Id); // V4: model key = ModelId, Id luôn 0
        Assert.Equal("gpt-x", entry.ModelId);
        Assert.True(sut.IsModelParked("gpt-x"));
    }

    [Fact]
    public void Park_WhenAlreadyParkedSameReason_KeepsParkedAtAndStaysSilent()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => changed++;
        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
        _time.Advance(TimeSpan.FromMinutes(5));

        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);

        var entry = Assert.Single(sut.GetEntries());
        Assert.Equal(_time.GetUtcNow().AddMinutes(-5), entry.ParkedAt); // giữ ParkedAt cũ
        Assert.Single(_log.Warns);  // không log lần 2
        Assert.Equal(1, changed);   // không event
    }

    [Fact]
    public void Park_WhenReasonChanged_UpdatesReasonFiresChangedKeepsParkedAt()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => changed++;
        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
        _time.Advance(TimeSpan.FromMinutes(5));

        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unauthorized);

        var entry = Assert.Single(sut.GetEntries());
        Assert.Equal(ManualRetryReason.Unauthorized, entry.Reason);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-5), entry.ParkedAt);
        Assert.Single(_log.Warns);  // transition log chỉ 1 lần
        Assert.Equal(2, changed);   // UI vẫn được báo để render lý do mới
    }

    [Fact]
    public void Unpark_WhenEntryExists_RemovesAndFiresChanged()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => changed++;
        sut.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);

        sut.Unpark(ManualRetryLevel.Model, 0, "m1");

        Assert.False(sut.IsModelParked("m1"));
        Assert.Empty(sut.GetEntries());
        Assert.Equal(2, changed);
        Assert.Single(_log.Warns); // Unpark không log — UI/ping tự log Info (§5)
    }

    [Fact]
    public void Unpark_WhenEntryMissing_IsNoOp()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => changed++;

        sut.Unpark(ManualRetryLevel.Provider, 99, "");

        Assert.Equal(0, changed);
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public void GetEntries_OrdersByParkedAtDescending()
    {
        var sut = CreateSut();
        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
        _time.Advance(TimeSpan.FromMinutes(1));
        sut.Park(ManualRetryLevel.Account, 2, "", ManualRetryReason.Unauthorized);
        _time.Advance(TimeSpan.FromMinutes(1));
        sut.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);

        var entries = sut.GetEntries();

        Assert.Equal(3, entries.Count);
        Assert.Equal("m1", entries[0].ModelId); // mới nhất trước
        Assert.Equal(ManualRetryLevel.Account, entries[1].Level);
        Assert.Equal(ManualRetryLevel.Provider, entries[2].Level);
    }

    [Fact]
    public void Park_Parallel400Times_LogsTransitionAndFiresChangedExactlyOnce()
    {
        var sut = CreateSut();
        var changed = 0;
        sut.Changed += () => Interlocked.Increment(ref changed);

        Parallel.For(0, 400, _ =>
            sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable));

        Assert.True(sut.IsProviderParked(1));
        // Check-then-act dưới lock: đúng 1 thread thấy transition
        Assert.Single(_log.Warns);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void IsParked_WhenTwoLevelsShareSameId_KeepsEntriesIndependent()
    {
        var sut = CreateSut();
        sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
        sut.Park(ManualRetryLevel.Account, 1, "", ManualRetryReason.Unauthorized);

        // Cùng id 1 nhưng 2 cấp là 2 entry độc lập
        Assert.True(sut.IsProviderParked(1));
        Assert.True(sut.IsAccountParked(1));
        Assert.False(sut.IsModelParked(""));
        Assert.False(sut.IsAccountParked(2));
        Assert.Equal(2, sut.GetEntries().Count);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
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
