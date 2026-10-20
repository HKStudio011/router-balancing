using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Tools;

/// <summary>
/// Model (dữ liệu thuần) để sinh entry config opencode v2 — tách khỏi entity DB
/// để generator không phụ thuộc EF; tầng service map entity + <c>ModelCapabilityView</c>
/// (Task 4) → record này, mọi danh sách đã parse sẵn.
/// </summary>
/// <param name="ModelId">Id model trong app, dùng làm key của object models.</param>
/// <param name="DisplayName">Tên hiển thị; <see langword="null"/> → dùng <paramref name="ModelId"/>.</param>
/// <param name="ContextWindow">Context window (token); <see langword="null"/> = không rõ.</param>
/// <param name="MaxOutputTokens">Trần token output; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsTools">Ba trạng thái: <see langword="null"/> = chưa biết (không emit),
/// <see langword="false"/> = KnownAbsent (emit <c>false</c>), true = emit <c>true</c>.</param>
/// <param name="SupportsVision">Model nhận ảnh; chỉ dùng làm fallback khi <paramref name="InputModalities"/>
/// không có khẳng định thuận.</param>
/// <param name="SupportsThink">Ba trạng thái reasoning; chỉ khi true mới emit <c>reasoning</c>.</param>
/// <param name="ThinkEfforts">Các mức effort đã biết (rỗng/null = chưa biết — R-T2-2 → không emit
/// key <c>effort</c>); phần tử <c>"toggle"</c> → emit <c>toggle: true</c>.</param>
/// <param name="InputModalities">Các modality input khẳng định thuận (rỗng/null = chưa biết →
/// fallback mặc định theo <paramref name="SupportsVision"/>).</param>
/// <param name="OutputModalities">Các modality output khẳng định thuận (rỗng/null → mặc định text).</param>
public sealed record OpenCodeConfigModel(string ModelId, string? DisplayName, int? ContextWindow, int? MaxOutputTokens, bool? SupportsTools, bool? SupportsVision, bool? SupportsThink, IReadOnlyList<string>? ThinkEfforts, IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities);

/// <summary>
/// Sinh JSON config opencode v2 (thuần, không I/O) cho custom provider trỏ về app:
/// <c>{"providers": {"router-balancing": {name, package, settings, models}}}</c>.
/// Không emit <c>cost</c>/<c>disabled</c>/<c>$schema</c> — config do app quản lý trọn vẹn.
/// </summary>
public static class OpenCodeConfigGenerator
{
    /// <summary>
    /// Id provider trong config opencode — id do app tự chọn (spec C2) để config user dán vào
    /// opencode khớp endpoint local của app; không ràng buộc với sidecar.
    /// </summary>
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
        // Ba trạng thái (C-I): null = chưa biết → omit; false = KnownAbsent → PHẢI emit false
        // (client cần biết "chắc chắn không tool" để không chọn nhầm model).
        if (model.SupportsTools is not null) capabilities["tools"] = model.SupportsTools.Value;
        if (model.SupportsThink is true) capabilities["reasoning"] = BuildReasoning(model.ThinkEfforts);

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

    /// <summary>
    /// Input modalities: phát danh sách khẳng định thuận từ view; null/rỗng = chưa biết →
    /// mặc định theo vision (opencode lúc nào cũng cần key input, không omit).
    /// </summary>
    private static JsonArray ResolveInputModalities(OpenCodeConfigModel model) =>
        model.InputModalities is { Count: > 0 }
            ? ToJsonArray(model.InputModalities)
            : model.SupportsVision is true ? ["text", "image"] : ["text"];

    /// <summary>Output modalities: như input nhưng mặc định <c>["text"]</c> khi chưa biết.</summary>
    private static JsonArray ResolveOutputModalities(OpenCodeConfigModel model) =>
        model.OutputModalities is { Count: > 0 }
            ? ToJsonArray(model.OutputModalities)
            : ["text"];

    /// <summary>
    /// Reasoning: literal <c>"toggle"</c> trong efforts → <c>toggle: true</c>;
    /// phần còn lại giữ nguyên thứ tự và giá trị (kể cả giá trị lạ ngoài low/medium/high — pass-through).
    /// R-T2-2: efforts null/rỗng = chưa biết → chỉ emit <c>supported</c>, không emit <c>effort</c>.
    /// </summary>
    private static JsonObject BuildReasoning(IReadOnlyList<string>? thinkEfforts)
    {
        var reasoning = new JsonObject { ["supported"] = true };
        if (thinkEfforts is not { Count: > 0 }) return reasoning;

        var toggle = false;
        var remaining = new List<string>(thinkEfforts.Count);
        foreach (var effort in thinkEfforts)
        {
            if (string.Equals(effort, "toggle", StringComparison.Ordinal)) toggle = true;
            else remaining.Add(effort);
        }
        if (toggle) reasoning["toggle"] = true;
        if (remaining.Count > 0) reasoning["effort"] = ToJsonArray(remaining);
        return reasoning;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
