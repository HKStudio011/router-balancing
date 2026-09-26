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
        _service = new ProviderService(_db, _protector);
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
    public async Task Create_WhenKeyProvided_EncryptsAndPersists()
    {
        var provider = await _service.CreateAsync(Draft());

        Assert.NotEqual("sk-secret", provider.ApiKeyEncrypted);
        Assert.Equal("sk-secret", _protector.Unprotect(provider.ApiKeyEncrypted));
        Assert.Equal("https://api.openai.com", provider.BaseUrl); // trailing slash đã trim
        Assert.Equal("OpenAI", provider.Name);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(provider.ApiKeyEncrypted, saved.ApiKeyEncrypted);
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
    public async Task Update_WhenApiKeyBlank_KeepsExistingKeyAndRefreshesTimestamp()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));
        var before = provider.UpdatedAt;

        await Task.Delay(10); // UpdatedAt có độ phân giải tick — đảm bảo khác biệt thực sự
        await _service.UpdateAsync(provider.Id, Draft(name: "Renamed", key: ""));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
        Assert.True(saved.UpdatedAt > before);
    }

    [Fact]
    public async Task Update_WhenApiKeyProvided_Reencrypts()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));

        await _service.UpdateAsync(provider.Id, Draft(key: "sk-new"));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("sk-new", _protector.Unprotect(saved.ApiKeyEncrypted));
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
