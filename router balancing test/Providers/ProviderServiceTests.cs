using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ProviderService _service;

    public ProviderServiceTests()
    {
        // Initialize trước mỗi test — TestDb là file trống, schema chưa có (pattern Phase 1)
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        // Task 3 mở rộng ctor — test CRUD dùng StubFactory handler không bao giờ được gọi
        _service = new ProviderService(_db, _protector, new NeverHttpFactory(), new NullLog());
    }

    /// <summary>HttpClientFactory ném nếu bị gọi — CRUD không được đụng network.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }

    public void Dispose() => _testDb.Dispose();

    private static ProviderDraft Draft(string name = "OpenAI", string key = "sk-secret") => new()
    {
        Name = name,
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com/",
        ApiKey = key,
        MaxConcurrent = 4,
    };

    [Fact]
    public async Task Create_WhenKeyProvided_CreatesDefaultAccountEncrypted()
    {
        var provider = await _service.CreateAsync(Draft());

        Assert.Equal("https://api.openai.com", provider.BaseUrl); // trailing slash đã trim
        Assert.Equal("OpenAI", provider.Name);

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        Assert.Equal("Default", account.Name);
        Assert.NotEqual("sk-secret", account.ApiKeyEncrypted);
        Assert.Equal("sk-secret", _protector.Unprotect(account.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Create_WhenKeyBlank_NoAccountCreated()
    {
        var provider = await _service.CreateAsync(Draft(key: string.Empty));

        using var db = _db.CreateDbContext();
        Assert.False(await db.ProviderAccounts.AnyAsync(a => a.ProviderId == provider.Id));
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com")]
    public async Task Create_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl(string input, string expected)
    {
        var provider = await _service.CreateAsync(new ProviderDraft
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = input,
            ApiKey = "sk",
            MaxConcurrent = 4,
        });

        Assert.Equal(expected, provider.BaseUrl);
        using var db = _db.CreateDbContext();
        Assert.Equal(expected, (await db.Providers.SingleAsync(p => p.Id == provider.Id)).BaseUrl);
    }

    [Fact]
    public async Task Update_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl()
    {
        var provider = await _service.CreateAsync(Draft());

        await _service.UpdateAsync(provider.Id, new ProviderDraft
        {
            Name = provider.Name,
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com/api/v1",
            ApiKey = string.Empty, // UpdateAsync bỏ qua draft.ApiKey hoàn toàn — key sống ở ProviderAccount
            MaxConcurrent = 4,
        });

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("https://api.example.com/api", saved.BaseUrl);
    }

    [Fact]
    public async Task ListAsync_WhenProvidersExist_IncludesModels()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        var list = await _service.ListAsync();

        var loaded = Assert.Single(list);
        Assert.Equal("gpt-4o", Assert.Single(loaded.Models).ModelId);
    }

    [Fact]
    public async Task ListAsync_IncludesAccounts()
    {
        var provider = await _service.CreateAsync(Draft());

        var list = await _service.ListAsync();

        var loaded = Assert.Single(list);
        Assert.Equal("Default", Assert.Single(loaded.Accounts).Name);
    }

    [Fact]
    public async Task GetAsync_WhenExists_IncludesModelsAndAccounts()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        var loaded = await _service.GetAsync(provider.Id);

        Assert.NotNull(loaded);
        Assert.Equal("gpt-4o", Assert.Single(loaded.Models).ModelId);
        Assert.Equal("Default", Assert.Single(loaded.Accounts).Name);
    }

    [Fact]
    public async Task Update_WhenApiKeyBlank_KeepsAccountKeyAndRefreshesTimestamp()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));
        var before = provider.UpdatedAt;

        await Task.Delay(10); // UpdatedAt có độ phân giải tick — đảm bảo khác biệt thực sự
        await _service.UpdateAsync(provider.Id, Draft(name: "Renamed", key: ""));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.True(saved.UpdatedAt > before);
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        Assert.Equal("sk-old", _protector.Unprotect(account.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Update_WhenApiKeyProvided_IgnoresKeyAndKeepsAccount()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));

        await _service.UpdateAsync(provider.Id, Draft(key: "sk-new"));

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        // Key chỉ đổi qua account CRUD — UpdateAsync bỏ qua draft.ApiKey (spec §4.2)
        Assert.Equal("sk-old", _protector.Unprotect(account.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Delete_WhenCalled_RemovesProviderAndCascadesModels()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        await _service.DeleteAsync(provider.Id);

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Providers.AnyAsync(p => p.Id == provider.Id));
        Assert.False(await db2.Models.AnyAsync(m => m.ProviderId == provider.Id));
    }

    [Fact]
    public async Task Delete_WhenUnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteAsync(999));
    }

    [Fact]
    public async Task SetEnabled_WhenToggled_Persists()
    {
        var provider = await _service.CreateAsync(Draft());

        await _service.SetEnabledAsync(provider.Id, false);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.False(saved.Enabled);
    }
}
