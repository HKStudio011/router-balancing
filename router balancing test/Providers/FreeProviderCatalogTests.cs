using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeProviderCatalogTests
{
    [Fact]
    public void Entries_OpenCodeFree_HasCatalogUrlAndCatalogKind()
    {
        var entry = FreeProviderCatalog.Entries.Single(e => e.DisplayName == "OpenCode Free");
        Assert.Equal(FreeDetectKind.OpenCodeCatalog, entry.DetectKind);
        Assert.Equal("https://models.opencode.ai/api.json", entry.CatalogUrl);
    }

    [Fact]
    public void FindByBaseUrl_CanonicalAndCaseInsensitive_Matches()
    {
        Assert.NotNull(FreeProviderCatalog.FindByBaseUrl("HTTPS://OpenCode.ai/zen"));
        Assert.Null(FreeProviderCatalog.FindByBaseUrl("https://example.com"));
    }
}
