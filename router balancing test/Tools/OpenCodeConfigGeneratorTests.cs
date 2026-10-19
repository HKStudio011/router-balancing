using System.Text.Json;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test generator JSON opencode v2 — thuần string/int/bool, không I/O, không DB.
/// Assert bằng <see cref="JsonDocument"/> (semantic) vì output emit JSON indent — so raw text rất giòn.
/// </summary>
public class OpenCodeConfigGeneratorTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Provider(string json) =>
        Root(json).GetProperty("providers").GetProperty(OpenCodeConfigGenerator.ProviderId);

    private static JsonElement Model(string json, string id) =>
        Provider(json).GetProperty("models").GetProperty(id);

    private static OpenCodeConfigModel Minimal(string id) =>
        new(id, null, null, null, null, false, false, null, null, null);

    [Fact]
    public void Generate_EmitsProviderShapeAndMinimalModel()
    {
        var json = OpenCodeConfigGenerator.Generate("http://127.0.0.1:8317/v1", null, [Minimal("m1")]);
        var p = Provider(json);
        Assert.Equal("Router Balancing", p.GetProperty("name").GetString());
        Assert.Equal("@opencode/ai/providers/openai-compatible", p.GetProperty("package").GetString());
        Assert.Equal("http://127.0.0.1:8317/v1", p.GetProperty("settings").GetProperty("baseURL").GetString());
        Assert.False(p.GetProperty("settings").TryGetProperty("apiKey", out _));
        Assert.False(Root(json).TryGetProperty("$schema", out _));             // C6: không emit $schema
        var m = Model(json, "m1");
        Assert.Equal("m1", m.GetProperty("name").GetString());                 // fallback ModelId
        Assert.Equal(new[] { "text" },                                  // semantic: emit JSON indent — đừng so raw text
            m.GetProperty("capabilities").GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.False(m.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.False(m.GetProperty("capabilities").TryGetProperty("reasoning", out _));
        Assert.False(m.TryGetProperty("limit", out _));                        // cả 2 null → không emit
        Assert.False(m.TryGetProperty("cost", out _));                         // U1
        Assert.False(m.TryGetProperty("disabled", out _));                     // U2
    }

    [Fact]
    public void Generate_VisionWithoutModalities_InputsTextImage()
    {
        var m = new OpenCodeConfigModel("m1", "Một", 100000, 8000, true, true, false, null, null, null);
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]);
        var caps = Model(json, "m1").GetProperty("capabilities");
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.True(caps.GetProperty("tools").GetBoolean());
        Assert.Equal(100000, Model(json, "m1").GetProperty("limit").GetProperty("context").GetInt32());
    }

    [Fact]
    public void Generate_ReasoningToggle_EmitsToggleAndEffort_AndExoticValuesPassThrough()
    {
        // ThinkEfforts = ["toggle","minimal","xhigh"] — giá trị lạ pass-through nguyên trạng (Review Focus)
        var m = new OpenCodeConfigModel("m1", null, null, null, null, false, true, "[\"toggle\",\"minimal\",\"xhigh\"]", null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.True(reasoning.GetProperty("toggle").GetBoolean());
        Assert.Equal(new[] { "minimal", "xhigh" },
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_ThinkingSupportedOnly_NoToggleNoEffort()
    {
        var m = new OpenCodeConfigModel("m1", null, null, null, null, false, true, null, null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.False(reasoning.TryGetProperty("toggle", out _));
        Assert.False(reasoning.TryGetProperty("effort", out _));
    }

    [Fact]
    public void Generate_ThinkingOff_OmitsReasoning()
    {
        var m = new OpenCodeConfigModel("m1", null, null, null, null, false, false, "[\"low\"]", null, null);
        Assert.False(Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void Generate_ApiKeyIncludedOnlyWhenSet()
    {
        var withKey = OpenCodeConfigGenerator.Generate("http://x/v1", "sk-rb-abc", [Minimal("m1")]);
        Assert.Equal("sk-rb-abc", Provider(withKey).GetProperty("settings").GetProperty("apiKey").GetString());
        var emptyKey = OpenCodeConfigGenerator.Generate("http://x/v1", "", [Minimal("m1")]);
        Assert.False(Provider(emptyKey).GetProperty("settings").TryGetProperty("apiKey", out _));
    }

    [Fact]
    public void Generate_DuplicateModelIds_FirstWins()
    {
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null,
            [Minimal("dup") with { DisplayName = "First" }, Minimal("dup") with { DisplayName = "Second" }]);
        Assert.Equal("First", Model(json, "dup").GetProperty("name").GetString());
        Assert.Single(Provider(json).GetProperty("models").EnumerateObject());
    }

    [Fact]
    public void Generate_EmptyModelList_EmitsEmptyModelsRecord()
    {
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, []);
        Assert.Empty(Provider(json).GetProperty("models").EnumerateObject());
    }

    [Fact]
    public void Generate_NullLimitFields_Omitted()
    {
        var m = new OpenCodeConfigModel("m1", null, null, 8000, null, false, false, null, null, null);
        var limit = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("limit");
        Assert.Equal(8000, limit.GetProperty("output").GetInt32());
        Assert.False(limit.TryGetProperty("context", out _));                  // context null → chỉ emit output
    }

    [Fact]
    public void Generate_ModelKeyOrder_NameCapabilitiesLimit()
    {
        // Thứ tự key cố định (name → capabilities → limit) — diff giữa các lần export ổn định
        var m = new OpenCodeConfigModel("m1", "Một", 1000, 100, null, false, false, null, null, null);
        var keys = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .EnumerateObject().Select(p => p.Name);
        Assert.Equal(new[] { "name", "capabilities", "limit" }, keys);
    }

    [Fact]
    public void Generate_ExplicitModalities_PassThroughVerbatim()
    {
        var m = new OpenCodeConfigModel("m1", null, null, null, null, false, false, null,
            "[\"text\",\"audio\"]", "[\"text\",\"audio\"]");
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.Equal(new[] { "text", "audio" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text", "audio" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_MalformedModalities_FallsBackToDefaults()
    {
        // Chuỗi modalities hỏng (không phải JSON array) → coi như không rõ, rơi về mặc định
        // (input theo SupportsVision, output ["text"]) — hợp đồng Task 4.
        var m = new OpenCodeConfigModel("m1", null, null, null, null, true, false, null, "not-json", """{"a":1}""");
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_MalformedThinkEfforts_NoToggleNoEffort()
    {
        // ThinkEfforts hỏng → vẫn reasoning {supported:true} nhưng không toggle/effort (parse fail = không rõ)
        var m = new OpenCodeConfigModel("m1", null, null, null, null, false, true, "garbage", null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.False(reasoning.TryGetProperty("toggle", out _));
        Assert.False(reasoning.TryGetProperty("effort", out _));
    }
}
