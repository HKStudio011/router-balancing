using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Usage;

namespace router_balancing_test.Server;

public class RequestUsageSinkTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _keys;
    private readonly IUsageDailyService _usage;
    private readonly ClientKeyRateLimiter _limiter = new(TimeProvider.System);

    public RequestUsageSinkTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _usage = new UsageDailyService(_db.CreateFactory(), TimeProvider.System);
        _keys = new ClientKeyService(_db.CreateFactory(), _usage);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Record_WithKeyId_PersistsTokensAndAdvancesTpmWindow()
    {
        var (key, _) = await _keys.CreateAsync(new ClientKeyDraft("app", null, 10));
        var sink = new RequestUsageSink(_keys, _usage, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(key.Id, providerAccountId: null, 6, 4);

        Assert.Equal(10, (await _keys.ListAsync()).Single().TokensUsed);
        // TPM window đã chạm 10 → request kế bị 429 (spec §5)
        Assert.False(_limiter.TryEnter(key.Id, null, 10).Allowed);
    }

    [Fact]
    public async Task Record_WithNullKeyId_DoesNothingAndDoesNotThrow()
    {
        var sink = new RequestUsageSink(_keys, _usage, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(null, providerAccountId: null, 100, 100); // auth mở — không có key để cộng

        Assert.Empty(await _keys.ListAsync());
    }

    [Fact]
    public async Task Record_WhenKeysServiceThrows_LogsErrorAndSwallows()
    {
        var log = new CapturingLog();
        var sink = new RequestUsageSink(
            new ThrowingKeys(new InvalidOperationException("db locked")), _usage, _limiter, log);

        await sink.RecordAsync(1, providerAccountId: null, 5, 5); // fail-open: không nổ ra caller (spec §10)

        Assert.Contains(log.Errors, m => m.Contains("usage counter"));
    }

    [Fact]
    public async Task Record_WhenRequestTokenCancelled_SkipsWithoutErrorLog()
    {
        var log = new CapturingLog();
        var sink = new RequestUsageSink(_keys, _usage, _limiter, log);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Client ngắt đúng lúc ghi (race sau khi upstream trả xong) — DB hủy theo token,
        // đây không phải lỗi DB (spec §10) nên không được sinh dòng Error.
        await sink.RecordAsync(1, providerAccountId: null, 5, 5, cts.Token);

        Assert.DoesNotContain(log.Errors, m => m.Contains("usage counter"));
    }

    [Fact]
    public async Task Record_WhenCancelledExceptionNotFromRequestToken_LogsError()
    {
        var log = new CapturingLog();
        var sink = new RequestUsageSink(
            new ThrowingKeys(new OperationCanceledException()), _usage, _limiter, log);

        await sink.RecordAsync(1, providerAccountId: null, 5, 5); // token còn sống — OCE không đến từ request hủy: vẫn là lỗi thật

        Assert.Contains(log.Errors, m => m.Contains("usage counter"));
    }

    [Fact]
    public async Task Record_WithProviderAccount_PersistsRequestAndTokens()
    {
        var accountId = await SeedAccountAsync();
        var sink = new RequestUsageSink(_keys, _usage, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(clientKeyId: null, accountId, 6, 4);

        await using var db = _db.CreateFactory().CreateDbContext();
        var row = await db.UsageDaily.SingleAsync(
            u => u.SubjectKind == UsageSubjectKind.ProviderAccount && u.SubjectId == accountId);
        Assert.Equal((1L, 10L), (row.Requests, row.Tokens));
    }

    [Fact]
    public async Task Record_WithProviderAccountButNoUsage_StillCountsRequest()
    {
        var accountId = await SeedAccountAsync();
        var sink = new RequestUsageSink(_keys, _usage, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(clientKeyId: null, accountId, 0, 0);

        await using var db = _db.CreateFactory().CreateDbContext();
        var row = await db.UsageDaily.SingleAsync(
            u => u.SubjectKind == UsageSubjectKind.ProviderAccount && u.SubjectId == accountId);
        Assert.Equal((1L, 0L), (row.Requests, row.Tokens));
    }

    [Fact]
    public async Task Record_WithClientKeyAndProviderAccount_WritesBoth()
    {
        var (key, _) = await _keys.CreateAsync(new ClientKeyDraft("app", null, 10));
        var accountId = await SeedAccountAsync();
        var sink = new RequestUsageSink(_keys, _usage, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(key.Id, accountId, 2, 3);

        Assert.Equal(5, (await _keys.ListAsync()).Single().TokensUsed);
        await using var db = _db.CreateFactory().CreateDbContext();
        var accountRow = await db.UsageDaily.SingleAsync(
            u => u.SubjectKind == UsageSubjectKind.ProviderAccount && u.SubjectId == accountId);
        Assert.Equal((1L, 5L), (accountRow.Requests, accountRow.Tokens));
    }

    private async Task<long> SeedAccountAsync()
    {
        await using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = $"p-{Guid.NewGuid():N}",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://p.test",
        };
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = string.Empty,
        });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Accounts[0].Id;
    }

    private sealed class ThrowingKeys(Exception error) : IClientKeyService
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
            => throw error;
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
