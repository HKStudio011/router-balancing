using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Tools;

/// <summary>
/// Model (dữ liệu thuần) để sinh entry config opencode v2 — tách khỏi entity DB
/// để generator không phụ thuộc EF; tầng service map entity + <c>ModelCapabilityView</c>
/// → record này, mọi danh sách đã parse sẵn.
/// </summary>
/// <param name="ModelId">Id model phía provider — dùng làm tên hiển thị fallback.</param>
/// <param name="DisplayName">Tên hiển thị; <see langword="null"/> → dùng <paramref name="ModelId"/>.</param>
/// <param name="ContextWindow">Context window (token); <see langword="null"/> = không rõ.</param>
/// <param name="MaxOutputTokens">Trần token output; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsTools">Ba trạng thái: <see langword="null"/> = chưa biết (không emit),
/// <see langword="false"/> = KnownAbsent (emit <c>false</c>), true = emit <c>true</c>.</param>
/// <param name="SupportsVision">Model nhận ảnh; chỉ dùng làm fallback khi <paramref name="InputModalities"/>
/// không có khẳng định thuận.</param>
/// <param name="SupportsThink">Ba trạng thái reasoning; true → <c>{supported:true}</c>,
/// false → <c>{supported:false}</c> (KnownAbsent), null → không emit.</param>
/// <param name="ThinkEfforts">Các mức effort đã biết (rỗng/null = chưa biết — R-T2-2 → không emit
/// key <c>effort</c>); phần tử <c>"toggle"</c> → emit <c>toggle: true</c>.</param>
/// <param name="InputModalities">Các modality input khẳng định thuận (rỗng/null = chưa biết →
/// fallback mặc định theo <paramref name="SupportsVision"/>).</param>
/// <param name="OutputModalities">Các modality output khẳng định thuận (rỗng/null → mặc định text).</param>
/// <param name="Key">Key trong object <c>models</c> — C-A: service truyền <c>&lt;Identifier&gt;/&lt;ModelId&gt;</c>;
/// <see langword="null"/> → dùng <paramref name="ModelId"/> (provider chưa có Identifier).</param>
public sealed record OpenCodeConfigModel(
    string ModelId, string? DisplayName, int? ContextWindow, int? MaxOutputTokens,
    bool? SupportsTools, bool? SupportsVision, bool? SupportsThink,
    IReadOnlyList<string>? ThinkEfforts, IReadOnlyList<string>? InputModalities,
    IReadOnlyList<string>? OutputModalities, string? Key = null);

/// <summary>
/// Entry combo (dữ liệu thuần) cho config opencode — U12: key object = tên combo để
/// opencode gửi đúng giá trị <c>model</c> mà proxy dùng để resolve combo.
/// Capability do service truyền vào đã là giao an toàn của các member (C-L).
/// </summary>
/// <param name="Name">Tên combo — cũng là key trong object <c>models</c> và là tên hiển thị.</param>
/// <param name="ContextWindow">Context window nhỏ nhất của member; <see langword="null"/> = chưa rõ.</param>
/// <param name="SupportsTools">Ba trạng thái tools (AND qua member).</param>
/// <param name="SupportsVision">Ba trạng thái vision (AND qua member).</param>
/// <param name="SupportsThink">Ba trạng thái reasoning (AND qua member).</param>
/// <param name="InputModalities">Giao modality input khẳng định thuận; null = chưa rõ.</param>
/// <param name="OutputModalities">Giao modality output khẳng định thuận; null = chưa rõ.</param>
public sealed record OpenCodeConfigCombo(
    string Name, int? ContextWindow, bool? SupportsTools, bool? SupportsVision, bool? SupportsThink,
    IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities);

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
    /// Sinh config opencode v2 (JSON indent) từ danh sách model + combo.
    /// </summary>
    /// <param name="baseUrl">Base URL endpoint OpenAI-compatible của app (luôn emit).</param>
    /// <param name="apiKey">API key cho endpoint; <see langword="null"/>/chuỗi rỗng → không emit <c>settings.apiKey</c>.</param>
    /// <param name="models">Model cần đưa vào config; key trùng giữ bản đầu tiên.</param>
    /// <param name="combos">Combo cần đưa vào config (mỗi combo một entry); key trùng với model bị bỏ.</param>
    /// <returns>JSON config dạng chuỗi (indent).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> hoặc <paramref name="models"/> là <see langword="null"/>.</exception>
    public static string Generate(
        string baseUrl, string? apiKey,
        IReadOnlyList<OpenCodeConfigModel> models,
        IReadOnlyList<OpenCodeConfigCombo>? combos = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(models);

        var root = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                [ProviderId] = BuildProviderObject(baseUrl, apiKey, models, combos),
            },
        };
        return root.ToJsonString(IndentedOptions);
    }

    /// <summary>
    /// Dựng object provider <c>router-balancing</c> (chưa bọc root) — dùng chung cho
    /// preview (<see cref="Generate"/>) và ghi file (<c>OpenCodeConfigService.WriteAsync</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl"/> hoặc <paramref name="models"/> là <see langword="null"/>.</exception>
    public static JsonObject BuildProviderObject(
        string baseUrl, string? apiKey,
        IReadOnlyList<OpenCodeConfigModel> models,
        IReadOnlyList<OpenCodeConfigCombo>? combos = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(models);

        var settings = new JsonObject { ["baseURL"] = baseUrl };
        if (!string.IsNullOrEmpty(apiKey)) settings["apiKey"] = apiKey;
        // C-D: bắt chước options.timeout:false của block cũ — không thì opencode cắt chat dài.
        settings["timeout"] = false;

        var modelsNode = new JsonObject();
        // Dedupe Ordinal first-wins: JSON object không cho phép key trùng; thứ tự models
        // (service sort free preset trước rồi Id) chốt bản thắng, không phụ thuộc thứ tự tick.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var key = model.Key ?? model.ModelId;
            if (!seenKeys.Add(key)) continue;
            modelsNode[key] = BuildEntry(
                model.DisplayName ?? model.ModelId, model.ContextWindow, model.MaxOutputTokens,
                model.SupportsTools, model.SupportsVision, model.SupportsThink, model.ThinkEfforts,
                model.InputModalities, model.OutputModalities);
        }

        // U12: combo đi sau model; trùng key (combo đặt tên bằng key model) → model thắng,
        // service báo key bị bỏ qua SkippedKeys. Deterministic, không phụ thuộc thứ tự tick.
        if (combos is not null)
        {
            foreach (var combo in combos)
            {
                if (!seenKeys.Add(combo.Name)) continue;
                modelsNode[combo.Name] = BuildEntry(
                    combo.Name, combo.ContextWindow, null,
                    combo.SupportsTools, combo.SupportsVision, combo.SupportsThink, thinkEfforts: null,
                    combo.InputModalities, combo.OutputModalities);
            }
        }

        return new JsonObject
        {
            ["name"] = ProviderName,
            ["package"] = ProviderPackage,
            ["settings"] = settings,
            ["models"] = modelsNode,
        };
    }

    /// <summary>Entry model/combo dùng chung: thứ tự key <c>name</c> → <c>capabilities</c> → <c>limit</c>.</summary>
    private static JsonObject BuildEntry(
        string displayName, int? contextWindow, int? maxOutputTokens,
        bool? supportsTools, bool? supportsVision, bool? supportsThink,
        IReadOnlyList<string>? thinkEfforts,
        IReadOnlyList<string>? inputModalities, IReadOnlyList<string>? outputModalities)
    {
        var capabilities = new JsonObject
        {
            ["input"] = ResolveInputModalities(inputModalities, supportsVision),
            ["output"] = ResolveOutputModalities(outputModalities),
        };
        // Ba trạng thái (C-I): null = chưa biết → omit; false = KnownAbsent → PHẢI emit false
        // (client cần biết "chắc chắn không tool" để không chọn nhầm model).
        if (supportsTools is not null) capabilities["tools"] = supportsTools.Value;
        if (supportsThink is true)
        {
            capabilities["reasoning"] = BuildReasoning(thinkEfforts);
        }
        else if (supportsThink is false)
        {
            // C-M: KnownAbsent chỉ có thể đến từ nguồn full-list hoặc user (ModelCapabilityWriter
            // chặn negative từ endpoint) — nói rõ "không hỗ trợ" thay vì để opencode tự suy.
            capabilities["reasoning"] = new JsonObject { ["supported"] = false };
        }

        var entry = new JsonObject { ["name"] = displayName };
        entry["capabilities"] = capabilities;
        if (contextWindow is not null || maxOutputTokens is not null)
        {
            var limit = new JsonObject();
            if (contextWindow is not null) limit["context"] = contextWindow.Value;
            if (maxOutputTokens is not null) limit["output"] = maxOutputTokens.Value;
            entry["limit"] = limit;
        }
        return entry;
    }

    /// <summary>
    /// Input modalities: phát danh sách khẳng định thuận; null/rỗng = chưa biết →
    /// mặc định theo vision (opencode lúc nào cũng cần key input, không omit).
    /// </summary>
    private static JsonArray ResolveInputModalities(IReadOnlyList<string>? modalities, bool? supportsVision) =>
        modalities is { Count: > 0 }
            ? ToJsonArray(modalities)
            : supportsVision is true ? ["text", "image"] : ["text"];

    /// <summary>Output modalities: như input nhưng mặc định <c>["text"]</c> khi chưa biết.</summary>
    private static JsonArray ResolveOutputModalities(IReadOnlyList<string>? modalities) =>
        modalities is { Count: > 0 }
            ? ToJsonArray(modalities)
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
