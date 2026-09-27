// router balancing test/Providers/ProviderAccountServiceTests.cs
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderAccountServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ProviderAccountService _service;

    public ProviderAccountServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        // CRUD dùng factory ném — không được phép đụng network
        _service = new ProviderAccountService(_db, _protector, new NeverHttpFactory(), new NullLog());
    }

    public void Dispose() => _testDb.Dispose();

    /// <summary>HttpClientFactory ném nếu bị gọi — CRUD không được đụng network.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }

    private ProviderAccountService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Trả 200 khi auth chứa "sk-ok", ngược lại 401 — mô phỏng 1 key sống 1 key chết.</summary>
    private sealed class KeyedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString() ?? string.Empty;
            var ok = auth.Contains("sk-ok", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private async Task<long> SeedProviderAsync(params (string Name, bool Enabled, int Priority)[] accounts)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
        };
        foreach (var (name, enabled, priority) in accounts)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = name,
                Enabled = enabled,
                Priority = priority,
                ApiKeyEncrypted = _protector.Protect($"sk-{name}"),
            });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private static ProviderAccountDraft Draft(long providerId, string name = "acct", string key = "sk-1") => new()
    {
        ProviderId = providerId,
        Name = name,
        ApiKey = key,
        Enabled = true,
        Weight = 100,
        Priority = 0,
    };

    [Fact]
    public async Task Create_WithKey_SavesDpapiEncrypted()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(Draft(providerId));

        Assert.NotEqual("sk-1", account.ApiKeyEncrypted);
        Assert.Equal("sk-1", _protector.Unprotect(account.ApiKeyEncrypted));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal(account.ApiKeyEncrypted, saved.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Create_DuplicateNameInSameProvider_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync();
        await _service.CreateAsync(Draft(providerId, name: "dup"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync(Draft(providerId, name: "dup")));
    }

    [Fact]
    public async Task Create_SameNameDifferentProviders_Ok()
    {
        var first = await SeedProviderAsync();
        var second = await SeedProviderAsync();
        await _service.CreateAsync(Draft(first, name: "shared"));

        var account = await _service.CreateAsync(Draft(second, name: "shared"));

        Assert.Equal(second, account.ProviderId);
    }

    [Fact]
    public async Task Create_WhenProviderMissing_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.CreateAsync(Draft(999)));
    }

    [Fact]
    public async Task Create_WithPatterns_SerializesJsonArray()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(new ProviderAccountDraft
        {
            ProviderId = providerId,
            Name = "scoped",
            ApiKey = "sk-1",
            ModelPatterns = ["gpt-4o*", "o3*"],
        });

        Assert.Equal("""["gpt-4o*","o3*"]""", account.ModelPatterns);
    }

    [Fact]
    public async Task Create_EmptyPattern_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId);
        draft.ModelPatterns = ["ok", "   "];

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    [Fact]
    public async Task Create_NameTooLong_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(Draft(providerId, name: new string('a', 101))));
    }

    [Theory]
    [InlineData("", "sk", 100, 0, null, null)]   // name rỗng
    [InlineData("A", "", 100, 0, null, null)]    // create bắt buộc key
    [InlineData("A", "sk", -1, 0, null, null)]   // weight < 0
    [InlineData("A", "sk", 10001, 0, null, null)] // weight > 10000
    [InlineData("A", "sk", 100, -1001, null, null)] // priority < -1000
    [InlineData("A", "sk", 100, 1001, null, null)]  // priority > 1000
    [InlineData("A", "sk", 100, 0, 0, null)]     // token limit <= 0
    [InlineData("A", "sk", 100, 0, null, -1)]    // request limit <= 0
    public async Task Create_InvalidDraft_ThrowsArgument(
        string name, string key, int weight, int priority, int? tokenLimit, int? requestLimit)
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId, name, key);
        draft.Weight = weight;
        draft.Priority = priority;
        draft.DailyTokenLimit = tokenLimit;
        draft.DailyRequestLimit = requestLimit;

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    [Fact]
    public async Task Update_EmptyApiKey_KeepsExistingKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "renamed", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Update_WhitespaceApiKey_KeepsExistingKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "renamed", key: "   ");

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
        Assert.Equal(1, await db.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task Update_NewKey_ReplacesEncrypted()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));

        await _service.UpdateAsync(account.Id, Draft(providerId, key: "sk-new"));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("sk-new", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Update_DuplicateNameExcludingSelf_Ok()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, name: "only"));
        var draft = Draft(providerId, name: "only", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft); // trùng chính nó vẫn hợp lệ

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task Update_WhenAccountMissing_ThrowsKeyNotFound()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateAsync(999, Draft(providerId)));
    }

    [Fact]
    public async Task Delete_LastAccount_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync(("only", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId)
                .Select(a => a.Id)
                .SingleAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(id));
        }
    }

    [Fact]
    public async Task Delete_NotLast_Succeeds()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId && a.Name == "a")
                .Select(a => a.Id)
                .SingleAsync();
            await _service.DeleteAsync(id);
        }

        using var db2 = _db.CreateDbContext();
        Assert.Equal(1, await db2.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task ListAsync_OrdersByPriorityThenName()
    {
        var providerId = await SeedProviderAsync(("mid", true, 5), ("first", true, 0), ("last", true, 9));

        var list = await _service.ListAsync(providerId);

        Assert.Equal(new[] { "first", "mid", "last" }, list.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task DeleteProvider_CascadesToAccounts()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));

        using (var db = _db.CreateDbContext())
        {
            var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
            db.Providers.Remove(provider);
            await db.SaveChangesAsync();
        }

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.ProviderAccounts.AnyAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task TestAllAsync_MixedResults_WritesEachAndProviderAnd()
    {
        var providerId = await SeedProviderAsync(("ok", true, 1), ("bad", true, 2));
        var service = ServiceWith(new KeyedHandler());

        var results = await service.TestAllAsync(providerId);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.AccountName == "ok").Success);
        var bad = results.Single(r => r.AccountName == "bad");
        Assert.False(bad.Success);
        Assert.Contains("401", bad.Message);

        using var db = _db.CreateDbContext();
        Assert.True(await db.ProviderAccounts
            .Where(a => a.ProviderId == providerId && a.Name == "ok")
            .Select(a => a.LastTestSuccess)
            .SingleAsync());
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.False(provider.LastTestSuccess); // AND của 2 account: 1 fail → false
        Assert.NotNull(provider.LastTestAt);
    }

    [Fact]
    public async Task TestAllAsync_NoEnabledAccounts_SetsProviderTestNull()
    {
        var providerId = await SeedProviderAsync(("off", false, 0));
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        var results = await service.TestAllAsync(providerId);

        Assert.Empty(results);
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.Null(provider.LastTestSuccess);
        Assert.Null(provider.LastTestAt);
    }

    [Fact]
    public async Task TestAllAsync_WhenProviderMissing_ThrowsKeyNotFound()
    {
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TestAllAsync(999));
    }
}
