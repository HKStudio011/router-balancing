using System.Net;
using System.Text;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

/// <summary>
/// Unit watchdog 3C (spec §3.5): probe due / backoff 60s×n floor Retry-After /
/// dừng ở MaxRetry / recover 2xx — TimeProvider fake, không chờ timer thật.
/// </summary>
public class ModelHealthWatchdogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly CapturingLog _log = new();
    private readonly FakeTime _time = new();
    private readonly DpapiSecretProtector _protector = new();
    private readonly ModelHealthStore _store;

    public ModelHealthWatchdogTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory());
        _store = new ModelHealthStore(_settings, _log, _time);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    // Mở fuse = MaxRetry lần RecordFailure; retryAfter (nếu có) chỉ ở lần cuối —
    // đúng transition floor lịch probe khi MỞ fuse (§3.6)
    private void OpenFuse(string modelId, TimeSpan? retryAfter = null)
    {
        var max = _settings.MaxRetry;
        for (var i = 1; i <= max; i++)
            _store.RecordFailure(modelId, i == max ? retryAfter : null);
    }

    private ModelHealthWatchdog CreateWatchdog(IUpstreamClient upstream,
        IComboResolver? resolver = null) =>
        new(_store, resolver ?? new StubResolver(Candidate()), upstream, _protector,
            _settings, _log, _time);

    // Candidate in-memory (không qua DB) — resolver stub trả thẳng
    private ModelCandidate Candidate(bool withKey = true)
    {
        var provider = new Provider
        {
            Id = 1,
            Name = "p1",
            BaseUrl = "https://api.openai.com",
            Type = ProviderType.OpenAI,
        };
        if (withKey)
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect("sk-live"),
            });
        return new ModelCandidate(provider, new Model { Id = 1, ModelId = "m1", Enabled = true });
    }

    // Candidate có account no-key (cột rỗng) — pin D8: resolver "" → VẪN probe, không chặn
    private ModelCandidate NoKeyCandidate()
    {
        var candidate = Candidate();
        candidate.Provider.Accounts[0].ApiKeyEncrypted = string.Empty;
        return candidate;
    }

    [Fact]
    public async Task ProbeDueAsync_WhenNextProbeNotDue_DoesNotCallUpstream()
    {
        OpenFuse("m1", retryAfter: TimeSpan.FromSeconds(600));
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // Fuse mở floor Retry-After 600s → chưa tới lịch, lần quét đầu không probe (§3.6)
        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(600),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbeSucceeds_RecoversModelAndLogsInfo()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(1, upstream.Calls);
        Assert.False(_store.IsManualRetry("m1")); // 2xx → RecordSuccess → về Healthy (§3.5)
        Assert.Contains(_log.Infos, i => i.Contains("phục hồi")); // log Info §5 do store ghi
    }

    [Fact]
    public async Task ProbeDueAsync_SendsMinimalChatBodyWithOneToken()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // Body probe = chat tối thiểu 1 token, stream:false — Dictionary serialize giữ snake_case (§3.5)
        var json = Encoding.UTF8.GetString(upstream.LastBody!);
        Assert.Contains("\"model\":\"m1\"", json);
        Assert.Contains("\"content\":\"ping\"", json);
        Assert.Contains("\"max_tokens\":1", json);
        Assert.Contains("\"stream\":false", json);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbe429_SchedulesSixtySecondBackoff()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Resp429());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // 60s × attemptsMade=1 (§3.5)
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);

        // Chưa tới lịch 60s → lần quét kế không probe lại
        await sut.ProbeDueAsync(CancellationToken.None);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbe429WithRetryAfter_FloorsBackoffToRetryAfter()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Resp429(retryAfter: "300"));
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // max(60s×1, 300s) = 300s — Retry-After thắng backoff (§3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenModelNotResolvable_RecordsProbeFailureWithoutCall()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream,
            new StubResolver(new SelectionFailure("m1", ResolveFailure.NotFound)));

        await sut.ProbeDueAsync(CancellationToken.None);

        // Model gỡ khỏi DB while fuse mở — không gọi upstream, vẫn lùi lịch (§3.5)
        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProviderHasNoEnabledKey_RecordsProbeFailureWithoutCall()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream, new StubResolver(Candidate(withKey: false)));

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenAccountHasNoKey_ProbesWithEmptyKey()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream, new StubResolver(NoKeyCandidate()));

        await sut.ProbeDueAsync(CancellationToken.None);

        // "" ≠ null: probe CHẠY với key rỗng (không auth) — test chặn null mới vào nhánh fail
        Assert.Equal(1, upstream.Calls);
        Assert.Equal(string.Empty, upstream.LastApiKey);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenUpstreamThrows_RecordsProbeFailureAndLogsWarn()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => throw new HttpRequestException("connection refused"));
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
        // Warn do store ghi (§5) — watchdog bắt lỗi rồi gọi RecordProbeFailure, không nuốt
        Assert.Contains(_log.Warns,
            w => w.Contains("Probe model 'm1'") && w.Contains("lần 1/3"));
    }

    [Fact]
    public async Task ProbeDueAsync_WhenMaxRetryReached_StopsProbing()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        _store.RecordFailure("m1"); // MaxRetry=1 → mở fuse ngay, lịch probe = now
        var upstream = new ScriptedUpstream(() => Resp429());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None); // probe fail → hết lượt (NextProbeAt = null)

        _time.Advance(TimeSpan.FromHours(2));
        await sut.ProbeDueAsync(CancellationToken.None);

        // Hết lượt probe tự động — ở lại ManualRetry, không probe vô hạn (§1.4/§3.5)
        Assert.Equal(1, upstream.Calls);
        Assert.Null(Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
        Assert.Contains(_log.Warns, w => w.Contains("hết lượt probe tự động"));
    }

    private sealed class StubResolver : IComboResolver
    {
        private readonly SelectionResult _result;

        // Brief gọi StubResolver(Candidate()) — ModelCandidate chưa phải SelectionResult,
        // bọc thành Success (watchdog chỉ lấy Candidates[0], không quan tâm Mode)
        public StubResolver(ModelCandidate candidate) =>
            _result = new SelectionSuccess([candidate], ComboMode.RoundRobin);

        public StubResolver(SelectionResult result) => _result = result;

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    private sealed class ScriptedUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public byte[]? LastBody { get; private set; }
        public string? LastApiKey { get; private set; }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastBody = body;
            LastApiKey = apiKey;
            return Task.FromResult(factory());
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(string? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"rate limited"}}""",
                Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

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
