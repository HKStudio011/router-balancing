using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class OpenCodeCatalogParserTests
{
    [Fact]
    public void ParseOpenCodeCatalog_FiltersFreeActiveAndMapsCapabilities()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);

        Assert.Equal(new[] { "big-pickle", "deepseek-v4.1-flash-free", "toggle-model" },
            models.Select(m => m.ModelId));

        var big = models.Single(m => m.ModelId == "big-pickle");
        Assert.Equal("Big Pickle", big.DisplayName);
        Assert.False(big.SupportsVision);            // attachment:false = biết chắc false (khác null = chưa rõ)
        Assert.True(big.SupportsTools);
        Assert.True(big.SupportsThink);
        Assert.Null(big.ThinkEfforts);               // reasoning_options rỗng
        Assert.Equal("[\"text\"]", big.InputModalities);
        Assert.Equal("[\"text\"]", big.OutputModalities);
        Assert.Equal(200000, big.ContextWindow);
        Assert.Equal(32000, big.MaxOutputTokens);

        var deepseek = models.Single(m => m.ModelId == "deepseek-v4.1-flash-free");
        Assert.True(deepseek.SupportsVision);
        Assert.Equal("[\"low\",\"high\",\"max\"]", deepseek.ThinkEfforts);
        Assert.Equal("[\"text\",\"image\"]", deepseek.InputModalities);
    }

    [Fact]
    public void ParseOpenCodeCatalog_ToggleOption_StoresToggleLiteral()
    {
        var toggle = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog)
            .Single(m => m.ModelId == "toggle-model");
        Assert.Equal("[\"toggle\"]", toggle.ThinkEfforts);
        Assert.Null(toggle.DisplayName);             // thiếu name → null
        Assert.Null(toggle.MaxOutputTokens);         // thiếu limit → null
    }

    [Fact]
    public void ParseOpenCodeCatalog_DeprecatedCostZero_Excluded()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);
        Assert.DoesNotContain(models, m => m.ModelId == "dead-model-free");
    }

    [Fact]
    public void ParseOpenCodeCatalog_HalfZeroOrPaidCost_Excluded()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);
        Assert.DoesNotContain(models, m => m.ModelId == "half-free");   // output 2 ≠ 0
        Assert.DoesNotContain(models, m => m.ModelId == "paid-model");
    }

    [Fact]
    public void ParseOpenCodeCatalog_MissingOpencodeModels_Throws()
    {
        Assert.Throws<FreeModelSyncException>(() =>
            FreeModelDetector.ParseOpenCodeCatalog("""{"data":[]}"""));
    }

    [Fact]
    public void ParseOpenCodeCatalog_RealCatalogSlice_ParsesLiveShape()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalogRealSlice);

        Assert.Equal(new[] { "big-pickle", "nemotron-3-ultra-free" }, models.Select(m => m.ModelId));
        var nemotron = models.Single(m => m.ModelId == "nemotron-3-ultra-free");
        Assert.Equal(1000000, nemotron.ContextWindow);
        Assert.Equal(128000, nemotron.MaxOutputTokens);
        Assert.Equal("[\"text\"]", nemotron.InputModalities);
    }
}
