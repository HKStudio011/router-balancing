using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class StaticCatalogMetadataProviderTests
{
    private static (Provider Provider, Model Model) Pair(string modelId)
    {
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        return (provider, new Model { ProviderId = 1, ModelId = modelId });
    }

    [Fact]
    public async Task FetchAsync_WhenGptModel_ReturnsCatalogMetadata()
    {
        var (provider, model) = Pair("gpt-4o-mini");
        var catalog = new StaticCatalogMetadataProvider();

        var meta = await catalog.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(128_000, meta.ContextWindow);
        Assert.True(meta.SupportsVision);
        Assert.False(meta.SupportsThink);
    }

    [Fact]
    public async Task FetchAsync_WhenClaudeModel_ReturnsVisionAndThink()
    {
        var (provider, model) = Pair("claude-3-7-sonnet-20250219");
        var catalog = new StaticCatalogMetadataProvider();

        var meta = await catalog.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(200_000, meta.ContextWindow);
        Assert.True(meta.SupportsThink);
        Assert.NotNull(meta.ThinkEfforts);
    }

    [Fact]
    public async Task FetchAsync_WhenUnknownModel_ReturnsNull()
    {
        var (provider, model) = Pair("some-internal-model-v2");
        var catalog = new StaticCatalogMetadataProvider();

        Assert.Null(await catalog.FetchAsync(provider, model));
    }
}
