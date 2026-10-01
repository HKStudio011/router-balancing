using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ClientKeyUsageSinkTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _keys;
    private readonly ClientKeyRateLimiter _limiter = new(TimeProvider.System);

    public ClientKeyUsageSinkTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _keys = new ClientKeyService(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Record_WithKeyId_PersistsTokensAndAdvancesTpmWindow()
    {
        var (key, _) = await _keys.CreateAsync(new ClientKeyDraft("app", null, 10));
        var sink = new ClientKeyUsageSink(_keys, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(key.Id, 6, 4);

        Assert.Equal(10, (await _keys.ListAsync()).Single().TokensUsed);
        // TPM window đã chạm 10 → request kế bị 429 (spec §5)
        Assert.False(_limiter.TryEnter(key.Id, null, 10).Allowed);
    }

    [Fact]
    public async Task Record_WithNullKeyId_DoesNothingAndDoesNotThrow()
    {
        var sink = new ClientKeyUsageSink(_keys, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(null, 100, 100); // auth mở — không có key để cộng

        Assert.Empty(await _keys.ListAsync());
    }

    [Fact]
    public async Task Record_WhenKeysServiceThrows_LogsErrorAndSwallows()
    {
        var log = new CapturingLog();
        var sink = new ClientKeyUsageSink(new ThrowingKeys(), _limiter, log);

        await sink.RecordAsync(1, 5, 5); // fail-open: không nổ ra caller (spec §10)

        Assert.Contains(log.Errors, m => m.Contains("usage counter"));
    }

    private sealed class ThrowingKeys : IClientKeyService
    {
        public event Action? KeysChanged { add { } remove { } }
        public Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ClientKey>>([]);
        public Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecordRequestAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default)
            => throw new InvalidOperationException("db locked");
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Errors { get; } = [];
        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) { }
        public void Warn(string message, LogCategory category = LogCategory.App) { }
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App)
            => Errors.Add(message);
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
