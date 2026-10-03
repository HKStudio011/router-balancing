using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderTestConnectionTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ProviderTestConnectionTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    /// <summary>Trả response tùy ý; ghi lại request để assert header.</summary>
    private sealed class FakeHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>Handler ném — mô phỏng timeout/mất mạng.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known.");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private ProviderService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    private async Task<Provider> SavedProviderAsync(ProviderType type = ProviderType.OpenAI)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = type,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
            Accounts =
            [
                new ProviderAccount
                {
                    Name = "Default",
                    ApiKeyEncrypted = _protector.Protect("sk-saved"),
                    Enabled = true,
                },
            ],
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    [Fact]
    public async Task TestConnection_When200_ReturnsSuccessAndPersistsResult()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync();
        var service = ServiceWith(handler);

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        // NotEqual(default) thay cho NotNull — DateTimeOffset là value type (xUnit2002)
        Assert.NotEqual(default(DateTimeOffset), result.At);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.True(saved.LastTestSuccess);
        Assert.NotNull(saved.LastTestAt);
    }

    [Fact]
    public async Task TestConnection_When401_ReturnsFailureWithStatusMessage()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized);
        var provider = await SavedProviderAsync();
        var service = ServiceWith(handler);

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.False(result.Success);
        Assert.Contains("401", result.Message);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.False(saved.LastTestSuccess);
    }

    [Fact]
    public async Task TestConnection_WhenHandlerThrows_ReturnsFailureNotThrow()
    {
        var provider = await SavedProviderAsync();
        var service = ServiceWith(new ThrowingHandler());

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.False(result.Success);
        Assert.Contains("No such host", result.Message);
    }

    [Fact]
    public async Task TestConnection_WhenOpenAi_SendsBearerHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(ProviderType.OpenAI);
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: "sk-from-form");

        Assert.Equal("Bearer sk-from-form",
            handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TestConnection_WhenAnthropic_SendsApiKeyAndVersionHeaders()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(ProviderType.Anthropic);
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: "sk-ant");

        Assert.Equal("sk-ant", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task TestConnection_WhenNoOverride_DecryptsFirstEnabledAccountKey()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(); // key đã lưu = "sk-saved"
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.Equal("Bearer sk-saved",
            handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TestConnection_WhenOverrideEmpty_ForcesNoKeyProbe()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(); // key đã lưu = "sk-saved"
        var service = ServiceWith(handler);

        // Override "" = ép probe không key dù account có key — UI test account no-key (D9);
        // null mới là fallback account
        var result = await service.TestConnectionAsync(provider, apiKeyOverride: "");

        Assert.True(result.Success);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task TestConnection_WhenNoKeyAccount_SucceedsWithoutAuthorization()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync();
        provider.Accounts[0].ApiKeyEncrypted = string.Empty; // account no-key
        var service = ServiceWith(handler);

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        // Trước khi fix: Unprotect("") ném → caught → Success=false
        Assert.True(result.Success);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task TestConnection_WhenNotSaved_DoesNotPersist()
    {
        // Provider chưa lưu (Id == 0) — result trả về nhưng không ghi DB
        var unsaved = new Provider
        {
            Name = "New",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
        };
        var service = ServiceWith(new FakeHandler(HttpStatusCode.OK));

        var result = await service.TestConnectionAsync(unsaved, "sk-new");

        Assert.True(result.Success);
        using var db = _db.CreateDbContext();
        // 4 preset free đã seed — chỉ đếm hàng user-created để giữ intent "unsaved không persist"
        Assert.Equal(0, await db.Providers.CountAsync(p => !p.IsPreset));
    }

    [Fact]
    public async Task TestConnection_WhenNoEnabledAccounts_SendsNoAuthorizationHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync();
        provider.Accounts[0].Enabled = false; // entity trong tay — resolver phải bỏ account tắt
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: null);

        // Không có account enabled → key "" → factory bỏ hẳn header auth (D7)
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }
}
