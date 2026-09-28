using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ModelResolverTests : IDisposable
{
    private readonly TestDb _db = new();

    public ModelResolverTests()
    {
        // Mọi DB test cần migrate trước khi seed — SQLite không có bảng nếu bỏ qua (repo convention).
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
        bool enabled = true, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Type = type,
            BaseUrl = "https://api.openai.com",
            Enabled = enabled,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelResolver CreateSut() => new(_db.CreateFactory());

    [Fact]
    public async Task Resolve_WhenModelExists_ReturnsSuccessWithProviderAndAccountsLoaded()
    {
        var id = SeedProvider("main", modelIds: ["gpt-4o-mini"]);
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = db.Providers.Find(id)!;
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                ApiKeyEncrypted = "enc",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var result = await CreateSut().ResolveAsync("gpt-4o-mini", default);

        var ok = Assert.IsType<ModelResolveSuccess>(result);
        Assert.Equal("main", ok.Provider.Name);
        Assert.Equal("gpt-4o-mini", ok.Model.ModelId);
        // Include(Accounts) bắt buộc — ProviderKeyResolver cần nav này (spec §2.3)
        Assert.Contains(ok.Provider.Accounts, a => a.Name == "a1");
    }

    [Fact]
    public async Task Resolve_WhenModelUnknown_ReturnsNotFound()
    {
        SeedProvider("main", modelIds: ["gpt-4o-mini"]);

        var result = await CreateSut().ResolveAsync("nope", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
        Assert.Equal("nope", fail.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenModelDisabled_ReturnsNotFound()
    {
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = new Provider { Name = "main", BaseUrl = "https://api.openai.com" };
            provider.Models.Add(new Model { ModelId = "m-off", Enabled = false });
            db.Providers.Add(provider);
            db.SaveChanges();
        }

        var result = await CreateSut().ResolveAsync("m-off", default);

        Assert.IsType<ModelResolveFailure>(result);
    }

    [Fact]
    public async Task Resolve_WhenProviderDisabled_ReturnsNotFound()
    {
        SeedProvider("off", enabled: false, modelIds: ["m1"]);

        var result = await CreateSut().ResolveAsync("m1", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenProviderAnthropic_ReturnsAnthropicNotSupported()
    {
        SeedProvider("claude", ProviderType.Anthropic, modelIds: ["sonnet-4"]);

        var result = await CreateSut().ResolveAsync("sonnet-4", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.AnthropicNotSupported, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenDuplicateModelIds_ReturnsLowestIdProvider()
    {
        // 2 provider cùng model id — 3A lấy Id nhỏ nhất, chọn provider thật là việc 3B (spec §2.3)
        SeedProvider("first", modelIds: ["shared-model"]);
        SeedProvider("second", modelIds: ["shared-model"]);

        var result = await CreateSut().ResolveAsync("shared-model", default);

        var ok = Assert.IsType<ModelResolveSuccess>(result);
        Assert.Equal("first", ok.Provider.Name);
    }

    [Fact]
    public async Task Resolve_WhenAnthropicProviderDisabled_ReturnsNotFound()
    {
        // Anthropic nhưng provider tắt → 404 trước, không phải 503 (spec §2.3 thứ tự check)
        SeedProvider("claude-off", ProviderType.Anthropic, enabled: false, modelIds: ["sonnet-4"]);

        var result = await CreateSut().ResolveAsync("sonnet-4", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }
}
