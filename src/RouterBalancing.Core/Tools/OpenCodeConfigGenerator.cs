using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Tools;

/// <summary>
/// Model (dữ liệu thuần) để sinh entry config opencode v2 — tách khỏi entity DB
/// để generator không phụ thuộc EF; tầng service (Task 5) map entity → record này.
/// </summary>
/// <param name="ModelId">Id model trong app, dùng làm key của object models.</param>
/// <param name="DisplayName">Tên hiển thị; <see langword="null"/> → dùng <paramref name="ModelId"/>.</param>
/// <param name="ContextWindow">Context window (token); <see langword="null"/> = không rõ.</param>
/// <param name="MaxOutputTokens">Trần token output; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsTools">Model gọi tool được; <see langword="null"/> = không rõ (không emit).</param>
/// <param name="SupportsVision">Model nhận ảnh; dùng khi <paramref name="InputModalities"/> không parse được.</param>
/// <param name="SupportsThink">Model suy nghĩ (reasoning) được — quyết định có emit <c>reasoning</c>.</param>
/// <param name="ThinkEfforts">JSON array mức think effort, ví dụ <c>["toggle","low"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="InputModalities">JSON array modalities đầu vào, ví dụ <c>["text","image"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="OutputModalities">JSON array modalities đầu ra, ví dụ <c>["text"]</c>; <see langword="null"/> = không rõ.</param>
public sealed record OpenCodeConfigModel(string ModelId, string? DisplayName, int? ContextWindow, int? MaxOutputTokens, bool? SupportsTools, bool SupportsVision, bool SupportsThink, string? ThinkEfforts, string? InputModalities, string? OutputModalities);

/// <summary>
/// Sinh JSON config opencode v2 (thuần, không I/O) cho custom provider trỏ về app:
/// <c>{"providers": {"router-balancing": {name, package, settings, models}}}</c>.
/// Không emit <c>cost</c>/<c>disabled</c>/<c>$schema</c> — config do app quản lý trọn vẹn.
/// </summary>
public static class OpenCodeConfigGenerator
{
    /// <summary>Id provider trong config opencode — trùng với custom provider app tạo cho sidecar.</summary>
    public const string ProviderId = "router-balancing";

    /// <summary>Tên hiển thị của provider trong opencode.</summary>
    public const string ProviderName = "Router Balancing";

    /// <summary>Package provider openai-compatible của opencode dùng cho endpoint local.</summary>
    public const string ProviderPackage = "@opencode/ai/providers/openai-compatible";

    // Options bất biến sau khởi tạo — dùng chung thay vì cấp phát mới mỗi lần generate.
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Sinh config opencode v2 (JSON indent) từ danh sách model.
    /// </summary>
    /// <param name="baseUrl">Base URL endpoint OpenAI-compatible của app (luôn emit).</param>
    /// <param name="apiKey">API key cho endpoint; <see langword="null"/>/chuỗi rỗng → không emit <c>settings.apiKey</c>.</param>
    /// <param name="models">Model cần đưa vào config; ModelId trùng lặp giữ bản đầu tiên.</param>
    /// <returns>JSON config dạng chuỗi (indent).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> hoặc <paramref name="models"/> là <see langword="null"/>.</exception>
    public static string Generate(string baseUrl, string? apiKey, IReadOnlyList<OpenCodeConfigModel> models)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(models);

        var settings = new JsonObject { ["baseURL"] = baseUrl };
        if (!string.IsNullOrEmpty(apiKey)) settings["apiKey"] = apiKey;

        var modelsNode = new JsonObject();
        // Dedupe Ordinal first-wins: object JSON không cho phép key trùng mà preset/catalog có thể
        // cùng ModelId — giữ bản đầu (spec chốt first-wins) để kết quả ổn định, không phụ thuộc thứ tự duyệt lại.
        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (!seenModelIds.Add(model.ModelId)) continue;
            modelsNode[model.ModelId] = BuildModel(model);
        }

        var root = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                [ProviderId] = new JsonObject
                {
                    ["name"] = ProviderName,
                    ["package"] = ProviderPackage,
                    ["settings"] = settings,
                    ["models"] = modelsNode,
                },
            },
        };
        return root.ToJsonString(IndentedOptions);
    }

    /// <summary>Entry model: thứ tự key <c>name</c> → <c>capabilities</c> → <c>limit</c>.</summary>
    private static JsonObject BuildModel(OpenCodeConfigModel model)
    {
        var capabilities = new JsonObject
        {
            ["input"] = ResolveInputModalities(model),
            ["output"] = ResolveOutputModalities(model),
        };
        if (model.SupportsTools is not null) capabilities["tools"] = model.SupportsTools.Value;
        if (model.SupportsThink) capabilities["reasoning"] = BuildReasoning(model.ThinkEfforts);

        var entry = new JsonObject { ["name"] = model.DisplayName ?? model.ModelId };
        entry["capabilities"] = capabilities;
        if (model.ContextWindow is not null || model.MaxOutputTokens is not null)
        {
            var limit = new JsonObject();
            if (model.ContextWindow is not null) limit["context"] = model.ContextWindow.Value;
            if (model.MaxOutputTokens is not null) limit["output"] = model.MaxOutputTokens.Value;
            entry["limit"] = limit;
        }
        return entry;
    }

    /// <summary>Input modalities: dùng chuỗi đã lưu nếu parse được, không thì mặc định theo vision.</summary>
    private static JsonArray ResolveInputModalities(OpenCodeConfigModel model)
    {
        var stored = TryParseStringArray(model.InputModalities);
        if (stored is not null) return ToJsonArray(stored);
        return model.SupportsVision ? ["text", "image"] : ["text"];
    }

    /// <summary>Output modalities: dùng chuỗi đã lưu nếu parse được, không thì mặc định <c>["text"]</c>.</summary>
    private static JsonArray ResolveOutputModalities(OpenCodeConfigModel model)
    {
        var stored = TryParseStringArray(model.OutputModalities);
        return stored is not null ? ToJsonArray(stored) : ["text"];
    }

    /// <summary>
    /// Reasoning: literal <c>"toggle"</c> trong efforts → <c>toggle: true</c>;
    /// phần còn lại giữ nguyên thứ tự và giá trị (kể cả giá trị lạ ngoài low/medium/high — pass-through).
    /// </summary>
    private static JsonObject BuildReasoning(string? thinkEfforts)
    {
        var reasoning = new JsonObject { ["supported"] = true };
        var efforts = TryParseStringArray(thinkEfforts);
        if (efforts is null) return reasoning;

        var toggle = false;
        var remaining = new List<string>(efforts.Count);
        foreach (var effort in efforts)
        {
            if (string.Equals(effort, "toggle", StringComparison.Ordinal)) toggle = true;
            else remaining.Add(effort);
        }
        if (toggle) reasoning["toggle"] = true;
        if (remaining.Count > 0) reasoning["effort"] = ToJsonArray(remaining);
        return reasoning;
    }

    /// <summary>
    /// Parse chuỗi JSON array lưu trong DB (modalities / think efforts) → list string.
    /// Hỏng/rỗng/toàn phần tử không phải string → <see langword="null"/> (coi như không rõ)
    /// để caller rơi về mặc định thay vì emit giá trị rác vào config.
    /// </summary>
    private static List<string>? TryParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var values = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String) values.Add(element.GetString()!);
            }
            return values.Count == 0 ? null : values;
        }
        catch (JsonException)
        {
            // Dữ liệu cũ/catalog đổi shape — không làm sập generate vì 1 field metadata hỏng.
            return null;
        }
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
