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

    [Fact]
    public void Entries_Ollama_IsCompleteCapabilityList()
    {
        // Task 6: tags API liệt kê đầy đủ capabilities cho mọi model Ollama → sync list
        // được phép sinh KnownAbsent (R-T5-2 hết hiệu lực cho Ollama).
        var entry = FreeProviderCatalog.Entries.Single(e => e.DisplayName == "Ollama Cloud Free");
        Assert.True(entry.CompleteCapabilityList);
    }
}
