using System.Text.Json;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeModelDetectorTests
{
    [Fact]
    public void Parse_OpenRouterFixture_KeepsZeroPricingAndFreeSuffix()
    {
        var models = FreeModelDetector.Parse(
            FreeModelFixtures.OpenRouter, FreeDetectKind.PricingZeroOrFreeSuffix);

        // "vendor/mispriced:free" vào dù pricing > 0 (D4: OR), "openai/gpt-4o" bị loại
        Assert.Equal(
            new[] { "inclusionai/ling-3.0-flash-sante:free", "stealth/space-bunny-alpha", "vendor/mispriced:free" },
            models.Select(m => m.ModelId));
        Assert.Equal(262144, models[0].ContextWindow);
        Assert.Equal("Ling 3.0 Flash Sante", models[0].DisplayName);
    }

    [Fact]
    public void Parse_OpenCodeFixture_KeepsOnlyFreeSuffix()
    {
        var models = FreeModelDetector.Parse(FreeModelFixtures.OpenCode, FreeDetectKind.FreeSuffix);

        Assert.Equal(new[] { "deepseek-v4-flash-free", "mimo-v2.5-free" }, models.Select(m => m.ModelId));
        Assert.Null(models[1].ContextWindow); // response không có context_length → null
    }

    [Fact]
    public void Parse_AllFree_ReturnsEveryModel()
    {
        var models = FreeModelDetector.Parse(FreeModelFixtures.Nvidia, FreeDetectKind.AllFree);

        Assert.Equal(2, models.Count);
        Assert.Null(models[0].DisplayName); // không có name → null
    }

    [Fact]
    public void Parse_MissingDataArray_ThrowsFreeModelSyncException()
        => Assert.Throws<FreeModelSyncException>(() => FreeModelDetector.Parse("""{"object":"list"}""", FreeDetectKind.AllFree));

    [Fact]
    public void Parse_MalformedJson_ThrowsJsonException()
        // ThrowsAny: JsonDocument.Parse ném JsonReaderException (subclass của JsonException)
        => Assert.ThrowsAny<JsonException>(() => FreeModelDetector.Parse("{not json", FreeDetectKind.AllFree));

    [Fact]
    public void Parse_EmptyDataArray_ReturnsEmpty()
        // Detector trả rỗng là hợp lệ — guard "không xoá gì" nằm ở service (§6.2.5)
        => Assert.Empty(FreeModelDetector.Parse("""{"data":[]}""", FreeDetectKind.AllFree));
}
