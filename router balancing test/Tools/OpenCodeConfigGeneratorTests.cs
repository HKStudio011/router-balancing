using System.Text.Json;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test generator JSON opencode v2 — thuần list/bool, không I/O, không DB.
/// Task 4: capability vào record dạng ĐÃ PARSE (IReadOnlyList&lt;string&gt;?) từ
/// ModelCapabilityView — generator không còn parse JSON-string (defect D9 hết đất tồn tại).
/// Assert bằng <see cref="JsonDocument"/> (semantic) vì output emit JSON indent — so raw text rất giòn.
/// </summary>
public class OpenCodeConfigGeneratorTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Provider(string json) =>
        Root(json).GetProperty("providers").GetProperty(OpenCodeConfigGenerator.ProviderId);

    private static JsonElement Model(string json, string id) =>
        Provider(json).GetProperty("models").GetProperty(id);

    private static readonly string[] DefaultReasoningEfforts =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    private static OpenCodeConfigModel Minimal(string id) =>
        new(id, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Generate_EmitsProviderShapeAndMinimalModel()
    {
        var json = OpenCodeConfigGenerator.Generate("http://127.0.0.1:8317/v1", null, [Minimal("m1")]);
        var p = Provider(json);
        Assert.Equal("Router Balancing", p.GetProperty("name").GetString());
        Assert.Equal("@opencode/ai/providers/openai-compatible", p.GetProperty("package").GetString());
        Assert.Equal("http://127.0.0.1:8317/v1", p.GetProperty("settings").GetProperty("baseURL").GetString());
        Assert.False(p.GetProperty("settings").TryGetProperty("apiKey", out _));
        Assert.False(p.GetProperty("settings").GetProperty("timeout").GetBoolean()); // C-D: không cắt chat dài
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
        var m = new OpenCodeConfigModel("m1", null, null, null, null, null, true, ["toggle", "minimal", "xhigh"], null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.True(reasoning.GetProperty("toggle").GetBoolean());
        Assert.Equal(new[] { "minimal", "xhigh" },
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_ThinkingWithoutEfforts_EmitsDefaultEfforts()
    {
        var m = new OpenCodeConfigModel("m1", null, null, null, null, null, true, null, null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.False(reasoning.TryGetProperty("toggle", out _));
        Assert.Equal(DefaultReasoningEfforts,
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_ToggleOnly_EmitsToggleAndDefaultEfforts()
    {
        // Nguồn chỉ nói model có công tắc think, không nêu mức effort → vẫn cần danh sách cho UI OpenCode.
        var m = new OpenCodeConfigModel("m1", null, null, null, null, null, true, ["toggle"], null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.True(reasoning.GetProperty("toggle").GetBoolean());
        Assert.Equal(DefaultReasoningEfforts,
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_ThinkingKnownAbsent_EmitsSupportedFalse()
    {
        // C-M: KnownAbsent đến từ full-list/user (writer bảo đảm) → nói rõ "không hỗ trợ".
        var m = Minimal("m1") with { SupportsThink = false };
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("reasoning", out var reasoning));
        Assert.False(reasoning.GetProperty("supported").GetBoolean());
        Assert.False(reasoning.TryGetProperty("toggle", out _));
        Assert.False(reasoning.TryGetProperty("effort", out _));
    }

    [Fact]
    public void Generate_ThinkingUnknown_OmitsReasoning()
    {
        // null = chưa biết → omit, khác false = đã biết là không (C-I).
        var m = Minimal("m1");
        Assert.False(Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void Generate_NegativeCapabilityFromKnownAbsent_EmitsFalse()
    {
        // Fact KnownAbsent (writer Task 2) → view Tools=false → generator PHẢI emit "tools": false
        // ("đã biết là không"), không omit — khác hẳn null = chưa biết (C-I ba trạng thái).
        var m = Minimal("m1") with
        {
            SupportsTools = false,
            SupportsVision = false,
            SupportsThink = false,
            InputModalities = [],
            OutputModalities = [],
        };
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("tools", out var tools));
        Assert.Equal(JsonValueKind.False, tools.ValueKind);
        Assert.False(tools.GetBoolean());
        // Reasoning KnownAbsent=false → C-M: emit supported:false (không omit — "đã biết là không")
        Assert.False(caps.GetProperty("reasoning").GetProperty("supported").GetBoolean());
        // Danh sách modality rỗng = không có khẳng định thuận → mặc định text, KHÔNG emit mảng rỗng
        Assert.Equal(new[] { "text" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_UnknownCapability_OmitsField()
    {
        // null = chưa biết → KHÔNG emit key (thiếu thông tin ≠ phát biểu false)
        var m = Minimal("m1") with { SupportsVision = true };
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.False(caps.TryGetProperty("tools", out _));                    // SupportsTools null → omit
        Assert.False(caps.TryGetProperty("reasoning", out _));                // SupportsThink null → omit
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
        var m = new OpenCodeConfigModel("m1", null, null, 8000, null, null, null, null, null, null);
        var limit = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("limit");
        Assert.Equal(8000, limit.GetProperty("output").GetInt32());
        Assert.False(limit.TryGetProperty("context", out _));                  // context null → chỉ emit output
    }

    [Fact]
    public void Generate_ModelKeyOrder_NameCapabilitiesLimit()
    {
        // Thứ tự key cố định (name → capabilities → limit) — diff giữa các lần export ổn định
        var m = new OpenCodeConfigModel("m1", "Một", 1000, 100, null, null, null, null, null, null);
        var keys = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .EnumerateObject().Select(p => p.Name);
        Assert.Equal(new[] { "name", "capabilities", "limit" }, keys);
    }

    [Fact]
    public void Generate_ExplicitModalities_PassThroughVerbatim()
    {
        var m = new OpenCodeConfigModel("m1", null, null, null, null, null, null, null,
            ["text", "audio"], ["text", "audio"]);
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.Equal(new[] { "text", "audio" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text", "audio" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_UnknownModalities_FallsBackToDefaults()
    {
        // List modalities null/rỗng = chưa biết (view không có khẳng định thuận) → rơi về mặc định
        // (input theo SupportsVision, output ["text"]) — hợp đồng Task 4.
        var m = new OpenCodeConfigModel("m1", null, null, null, null, true, null, null, [], []);
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1").GetProperty("capabilities");
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_EmptyThinkEfforts_EmitsDefaultEfforts()
    {
        // Danh sách rỗng = chưa biết mức nào; generator vẫn cần bộ effort mặc định cho UI OpenCode.
        var m = new OpenCodeConfigModel("m1", null, null, null, null, null, true, [], null, null);
        var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
            .GetProperty("capabilities").GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.False(reasoning.TryGetProperty("toggle", out _));
        Assert.Equal(DefaultReasoningEfforts,
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Generate_ModelWithKey_UsesKeyForObjectKey_KeepsNameFallback()
    {
        // C-A: key config = "<Identifier>/<ModelId>"; name vẫn là tên người đọc được.
        var m = Minimal("big-pickle") with { Key = "opencode-free/big-pickle" };
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]);
        var entry = Model(json, "opencode-free/big-pickle");
        Assert.Equal("big-pickle", entry.GetProperty("name").GetString());
        Assert.False(Provider(json).GetProperty("models").TryGetProperty("big-pickle", out _));
    }

    [Fact]
    public void Generate_Combo_EmitsSingleEntryKeyedByName()
    {
        // U12: combo = một entry duy nhất, key = tên combo (không prefix Identifier).
        var combo = new OpenCodeConfigCombo(
            "unsloth-studio", 64_000, true, true, true, ["text", "image"], ["text"]);
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, [Minimal("m1")], [combo]);

        var entry = Model(json, "unsloth-studio");
        Assert.Equal("unsloth-studio", entry.GetProperty("name").GetString());
        var caps = entry.GetProperty("capabilities");
        Assert.True(caps.GetProperty("tools").GetBoolean());
        var reasoning = caps.GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("supported").GetBoolean());
        Assert.Equal(DefaultReasoningEfforts,
            reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(64_000, entry.GetProperty("limit").GetProperty("context").GetInt32());
        Assert.False(entry.GetProperty("limit").TryGetProperty("output", out _));
        Assert.Single(Provider(json).GetProperty("models").EnumerateObject(), p => p.Name == "unsloth-studio");
    }

    [Fact]
    public void Generate_ComboKeyCollidesWithModelKey_ModelWins()
    {
        // Trùng key (combo đặt tên đúng bằng key model) — first-wins theo thứ tự models trước combos.
        var combo = new OpenCodeConfigCombo("combo-a", null, null, null, null, null, null);
        var json = OpenCodeConfigGenerator.Generate("http://x/v1", null,
            [Minimal("combo-a") with { DisplayName = "Model" }], [combo]);

        Assert.Equal("Model", Model(json, "combo-a").GetProperty("name").GetString());
        Assert.Single(Provider(json).GetProperty("models").EnumerateObject());
    }

    [Fact]
    public void Generate_ComboUnknownModalities_FallsBackToDefaults()
    {
        var combo = new OpenCodeConfigCombo("c1", null, null, true, null, null, null);
        var caps = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [], [combo]), "c1")
            .GetProperty("capabilities");
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "text" },
            caps.GetProperty("output").EnumerateArray().Select(e => e.GetString()));
    }
}
