using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderPingServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly CapturingLog _log = new();
    private readonly ManualRetryStore _store;
    private readonly AppSettingsService _settings;

    public ProviderPingServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _store = new ManualRetryStore(_log, TimeProvider.System);
        _settings = new AppSettingsService(_db);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _testDb.Dispose();
    }

    /// <summary>Response scriptable; ghi URL + auth header để assert; ném được (mạng/timeout).</summary>
    private sealed class ProbeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Throw { get; set; }
        public List<string> SentUrls { get; } = [];
        public List<string?> SentAuthHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SentUrls.Add(request.RequestUri!.ToString());
            SentAuthHeaders.Add(request.Headers.Authorization?.ToString());
            if (Throw is { } ex) throw ex;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class StubFactory(ProbeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Bổ sung 5 member còn lại của ILogService (Write/LogRequestUsage/Query/Count/LogAdded)
    // — brief chỉ nêu Info/Warn/Error; pattern no-op lấy từ ManualRetryStoreTests.CapturingLog
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

    private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
        bool enabled = true, string apiKey = "sk-test")
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Type = type,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
            Enabled = enabled,
            Accounts =
            [
                new ProviderAccount
                {
                    Name = $"{name}-a",
                    Enabled = true,
                    ApiKeyEncrypted = apiKey.Length > 0 ? _protector.Protect(apiKey) : string.Empty,
                },
            ],
        };
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ProviderPingService CreateService(ProbeHandler handler) =>
        new(_db, new StubFactory(handler), _protector, _settings, _store, _log);

    [Fact]
    public async Task PingAllAsync_WhenUpstream2xx_KeepsStateQuietAndSendsGetModels()
    {
        var handler = new ProbeHandler();
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.False(_store.IsProviderParked(id));
        Assert.EndsWith("/v1/models", Assert.Single(handler.SentUrls));
        Assert.Equal("Bearer sk-test", Assert.Single(handler.SentAuthHeaders));
        Assert.Empty(_log.Infos);
        Assert.Empty(_log.Warns);
        Assert.Empty(_log.Errors);
        Assert.Null(ProxyTarget.Current.Value); // reset sau call (finally)
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task PingAllAsync_WhenUpstreamUnauthorized_ParksProvider(int status)
    {
        var handler = new ProbeHandler { Status = (HttpStatusCode)status };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.True(_store.IsProviderParked(id));
        var entry = Assert.Single(_store.GetEntries());
        Assert.Equal(ManualRetryLevel.Provider, entry.Level);
        Assert.Equal(ManualRetryReason.Unauthorized, entry.Reason);
        // 2 Warn: store (transition) + ping service (spec §5) — assert câu của ping
        Assert.Contains(_log.Warns,
            w => w.Contains($"Ping provider 'p1' thất bại (HTTP {status}) — đưa vào danh sách retry thủ công"));
    }

    [Fact]
    public async Task PingAllAsync_WhenUpstream404_ParksProviderWithNotFoundReason()
    {
        var handler = new ProbeHandler { Status = HttpStatusCode.NotFound };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.True(_store.IsProviderParked(id));
        Assert.Equal(ManualRetryReason.NotFound, Assert.Single(_store.GetEntries()).Reason);
        Assert.Contains(_log.Warns, w => w.Contains("Ping provider 'p1' thất bại (HTTP 404)"));
    }

    [Fact]
    public async Task PingAllAsync_WhenNetworkError_ParksProviderWithUnreachableReason()
    {
        var handler = new ProbeHandler { Throw = new HttpRequestException("No such host is known.") };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.True(_store.IsProviderParked(id));
        Assert.Equal(ManualRetryReason.Unreachable, Assert.Single(_store.GetEntries()).Reason);
        Assert.Contains(_log.Warns, w => w.Contains("Ping provider 'p1' thất bại (lỗi mạng)"));
    }

    [Fact]
    public async Task PingAllAsync_WhenTimeout_ParksProviderWithUnreachableReason()
    {
        // TaskCanceledException = timeout từ ProviderProbeTimeoutHandler — không phải host stop
        // (ct = None → catch thứ nhất có filter false, rơi vào catch TCE)
        var handler = new ProbeHandler { Throw = new TaskCanceledException("Provider probe exceeded 60s timeout.") };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.True(_store.IsProviderParked(id));
        Assert.Equal(ManualRetryReason.Unreachable, Assert.Single(_store.GetEntries()).Reason);
        Assert.Contains(_log.Warns, w => w.Contains("lỗi mạng"));
    }

    [Fact]
    public async Task PingAllAsync_WhenUpstream429_DoesNotPark()
    {
        var handler = new ProbeHandler { Status = HttpStatusCode.TooManyRequests };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.False(_store.IsProviderParked(id));
        Assert.Single(handler.SentUrls); // vẫn ping — chỉ không park
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public async Task PingAllAsync_WhenUpstream500_DoesNotPark()
    {
        var handler = new ProbeHandler { Status = HttpStatusCode.InternalServerError };
        var id = SeedProvider("p1");
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.False(_store.IsProviderParked(id));
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOff_SkipsPing()
    {
        var handler = new ProbeHandler();
        var id = SeedProvider("p1");
        _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
        _log.Warns.Clear(); // chỉ quan tâm log phát sinh MỚI khi ping
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None); // PingParkedProviders mặc định false

        Assert.Empty(handler.SentUrls);
        Assert.True(_store.IsProviderParked(id));
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOn2xx_UnparksAndLogsInfo()
    {
        var handler = new ProbeHandler();
        var id = SeedProvider("p1");
        _settings.Set(SettingsKeys.PingParkedProviders, true);
        _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
        _log.Warns.Clear();
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.False(_store.IsProviderParked(id));
        Assert.Contains(_log.Infos,
            i => i.Contains("Provider 'p1' phục hồi qua ping — tự gỡ khỏi danh sách"));
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOnStillFailing_KeepsParkWithoutDuplicateLog()
    {
        var handler = new ProbeHandler { Status = HttpStatusCode.Unauthorized };
        var id = SeedProvider("p1");
        _settings.Set(SettingsKeys.PingParkedProviders, true);
        _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
        _log.Warns.Clear();
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.True(_store.IsProviderParked(id)); // giữ nguyên — không đổi trạng thái
        Assert.Single(handler.SentUrls);          // vẫn ping (flag bật)
        Assert.Empty(_log.Warns);                 // không lặp log mỗi tick
    }

    [Fact]
    public async Task PingAllAsync_WhenProviderAnthropic_SkipsPing()
    {
        var handler = new ProbeHandler();
        var id = SeedProvider("p1", ProviderType.Anthropic);
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.Empty(handler.SentUrls);
        Assert.False(_store.IsProviderParked(id));
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public async Task PingAllAsync_WhenNoEnabledUnparkedAccount_SkipsProvider()
    {
        var handler = new ProbeHandler();
        var id = SeedProvider("p1");
        using (var db = _db.CreateDbContext())
        {
            var account = db.ProviderAccounts.Single(a => a.ProviderId == id);
            account.Enabled = false;
            db.SaveChanges();
        }
        var service = CreateService(handler);

        await service.PingAllAsync(CancellationToken.None);

        Assert.Empty(handler.SentUrls); // không còn TK hợp lệ → bỏ qua lượt ping
        Assert.False(_store.IsProviderParked(id));
        Assert.Empty(_log.Warns);
    }
}
