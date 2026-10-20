using System.Globalization;
using System.Text.Json;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Model free sau khi parse — metadata/capabilities lấy từ API nếu response có;
/// field thiếu → null (chưa rõ), không đoán (spec opencode-tools-page §6.2).
/// R-T5-1: efforts/modalities là danh sách typed, KHÔNG phải chuỗi JSON — consumer
/// (FreeModelSyncService) ghép thẳng vào CapabilityPatch, không parse lại.
/// </summary>
public sealed record FreeModelInfo(
    string ModelId,
    string? DisplayName,
    int? ContextWindow,
    bool? SupportsTools = null,
    bool? SupportsVision = null,
    bool? SupportsThink = null,
    IReadOnlyList<string>? ThinkEfforts = null,
    IReadOnlyList<string>? InputModalities = null,
    IReadOnlyList<string>? OutputModalities = null,
    int? MaxOutputTokens = null);

/// <summary>
/// Parse response list model (OpenAI shape <c>{"data":[...]}</c>) và lọc free theo
/// <see cref="FreeDetectKind"/> — pure function, không I/O (spec provider-free §6.3).
/// </summary>
public static class FreeModelDetector
{
    /// <summary>
    /// Lọc danh sách model free theo <paramref name="kind"/>.
    /// </summary>
    /// <param name="json">Body response của models endpoint.</param>
    /// <param name="kind">Cách detect free theo provider.</param>
    /// <returns>Các model free, giữ nguyên thứ tự trong response.</returns>
    /// <exception cref="JsonException">JSON malformed.</exception>
    /// <exception cref="FreeModelSyncException">Response thiếu mảng <c>data</c>.</exception>
    public static IReadOnlyList<FreeModelInfo> Parse(string json, FreeDetectKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new FreeModelSyncException("Model list response is missing the data array.");
        }

        var result = new List<FreeModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement)) continue;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!IsFree(kind, id, item)) continue;
            // OpenRouter là preset free DUY NHẤT list mang sẵn capability (spec capability-model §7) —
            // các kind khác (FreeSuffix/AllFree) list không có thông tin, giữ nguyên shape tối thiểu.
            result.Add(kind == FreeDetectKind.PricingZeroOrFreeSuffix
                ? ParseOpenRouterItem(id!, item)
                : new FreeModelInfo(id!, DisplayNameOf(item), ContextWindowOf(item)));
        }
        return result;
    }

    /// <summary>
    /// Parse catalog <c>models.opencode.ai/api.json</c> (spec opencode-tools-page §6.2) — pure function,
    /// giữ nguyên thứ tự model trong catalog.
    /// </summary>
    /// <param name="json">Body của catalog.</param>
    /// <returns>Model free (cost input/output đều 0, status khác deprecated) kèm capabilities map từ entry.</returns>
    /// <exception cref="JsonException">JSON malformed.</exception>
    /// <exception cref="FreeModelSyncException">Root thiếu object <c>opencode.models</c>.</exception>
    public static IReadOnlyList<FreeModelInfo> ParseOpenCodeCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("opencode", out var opencode)
            || opencode.ValueKind != JsonValueKind.Object
            || !opencode.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Object)
        {
            throw new FreeModelSyncException("Catalog response is missing opencode.models.");
        }

        var result = new List<FreeModelInfo>();
        foreach (var model in models.EnumerateObject())
        {
            // Value không phải object (shape lạ) — bỏ qua thay vì đoán
            if (model.Value.ValueKind != JsonValueKind.Object) continue;
            if (!IsFreeCatalogModel(model.Value)) continue;
            result.Add(new FreeModelInfo(
                ModelId: model.Name, // tên property = key của opencode.models
                DisplayName: DisplayNameOf(model.Value),
                ContextWindow: LimitOf(model.Value, "context"),
                SupportsTools: BoolPropertyOf(model.Value, "tool_call"),
                SupportsVision: BoolPropertyOf(model.Value, "attachment"),
                SupportsThink: BoolPropertyOf(model.Value, "reasoning"),
                ThinkEfforts: ThinkEffortsOf(model.Value),
                InputModalities: ModalitiesOf(model.Value, "input"),
                OutputModalities: ModalitiesOf(model.Value, "output"),
                MaxOutputTokens: LimitOf(model.Value, "output")));
        }
        return result;
    }

    /// <summary>
    /// Catalog entry free (spec §6.2): cost input VÀ output đều 0 — thiếu cost/1 field = không free;
    /// status "deprecated" bị loại, thiếu status = active.
    /// </summary>
    private static bool IsFreeCatalogModel(JsonElement item)
    {
        if (item.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!item.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return cost.TryGetProperty("input", out var input) && IsZero(input)
            && cost.TryGetProperty("output", out var output) && IsZero(output);
    }

    /// <summary>bool? từ field catalog — thiếu hoặc không phải true/false → null (chưa rõ, không đoán).</summary>
    private static bool? BoolPropertyOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>limit.&lt;name&gt; number → int?; thiếu/không phải number → null.</summary>
    private static int? LimitOf(JsonElement item, string name) =>
        item.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object
        && limit.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>
    /// reasoning_options → danh sách (spec §5): "toggle" thành literal "toggle" (công tắc think),
    /// values của option "effort" đầu tiên; cả hai vắng → null. Toggle luôn đứng đầu list.
    /// </summary>
    private static IReadOnlyList<string>? ThinkEffortsOf(JsonElement item)
    {
        if (!item.TryGetProperty("reasoning_options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hasToggle = false;
        var effortSeen = false;
        var efforts = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            var type = option.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (string.Equals(type, "toggle", StringComparison.OrdinalIgnoreCase))
            {
                hasToggle = true;
            }
            else if (!effortSeen && string.Equals(type, "effort", StringComparison.OrdinalIgnoreCase))
            {
                effortSeen = true;
                if (option.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.String) efforts.Add(value.GetString()!);
                    }
                }
            }
        }

        if (!hasToggle && efforts.Count == 0) return null;

        var result = new List<string>(efforts.Count + (hasToggle ? 1 : 0));
        if (hasToggle) result.Add("toggle");
        result.AddRange(efforts);
        return result;
    }

    /// <summary>modalities.input/.output → danh sách token; thiếu/rỗng/toàn phần tử không phải string → null.</summary>
    private static IReadOnlyList<string>? ModalitiesOf(JsonElement item, string direction)
    {
        if (!item.TryGetProperty("modalities", out var modalities) || modalities.ValueKind != JsonValueKind.Object
            || !modalities.TryGetProperty(direction, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String) result.Add(value.GetString()!);
        }
        return result.Count == 0 ? null : result;
    }

    private static bool IsFree(FreeDetectKind kind, string id, JsonElement item) => kind switch
    {
        FreeDetectKind.AllFree => true,
        FreeDetectKind.FreeSuffix => id.EndsWith("-free", StringComparison.Ordinal),
        // D4: pricing = 0 HOẶC pattern ":free" — OR, không AND (id :free thì free kể cả pricing > 0)
        FreeDetectKind.PricingZeroOrFreeSuffix =>
            id.EndsWith(":free", StringComparison.Ordinal) || IsZeroPricing(item),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown free detect kind."),
    };

    /// <summary>OpenRouter: cả prompt lẫn completion phải tồn tại và parse về 0 — thiếu 1 field = không coi free (chủ động).</summary>
    private static bool IsZeroPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return pricing.TryGetProperty("prompt", out var prompt) && IsZero(prompt)
            && pricing.TryGetProperty("completion", out var completion) && IsZero(completion);
    }

    /// <summary>
    /// Item OpenRouter → FreeModelInfo kèm phát biểu capability lấy ngay trong list (spec capability-model §7).
    /// Luật lấy từ shape API thật (probe 2026-10-20):
    /// <list type="bullet">
    /// <item><c>supported_parameters</c> là full-list (C-P): chứa token "tools" → true,
    /// mảng có mặt mà không chứa → false; THẾ NHƯNG mảng/field vắng hẳn (fixture cũ, API đổi shape)
    /// → không phát biểu gì (null) — vắng field ≠ khẳng định "không".</item>
    /// <item>Reasoning = token "reasoning" trong supported_parameters (match tuyệt đối —
    /// "include_reasoning"/"reasoning_effort" không được tính), HOẶC field <c>reasoning</c>:
    /// boolean true (shape cũ từng tồn tại) hoặc object (shape hiện tại — OpenRouter chỉ
    /// sinh object này cho model hỗ trợ reasoning). Field boolean false → khẳng định "không".
    /// R-T5-4: <c>reasoning.supported_efforts</c> (mảng string) → ThinkEfforts; object có mà
    /// efforts vắng/rỗng → chỉ phát biểu Reasoning, không bịa effort nào.</item>
    /// <item><c>architecture.input_modalities/output_modalities</c> → danh sách token thô;
    /// token ngoài từ vựng 4 loại ("file"…) được giữ nguyên ở đây, lọc ở bước ghép patch.</item>
    /// </list>
    /// Không dùng <c>architecture.modality</c> làm tín hiệu reasoning: giá trị thật luôn dạng
    /// "in-&gt;out" (vd "text+image-&gt;text") và mô tả modality, không phải reasoning —
    /// vision đã có trong input_modalities. (Xem report Task 5 về lệch pin này.)
    /// </summary>
    private static FreeModelInfo ParseOpenRouterItem(string id, JsonElement item)
    {
        bool? tools = null;
        bool? reasoning = null;
        IReadOnlyList<string>? efforts = null;
        if (item.TryGetProperty("supported_parameters", out var supported)
            && supported.ValueKind == JsonValueKind.Array)
        {
            var tokens = new List<string>();
            foreach (var token in supported.EnumerateArray())
            {
                if (token.ValueKind == JsonValueKind.String) tokens.Add(token.GetString()!);
            }
            tools = tokens.Contains("tools", StringComparer.Ordinal);
            reasoning = tokens.Contains("reasoning", StringComparer.Ordinal);
        }
        if (item.TryGetProperty("reasoning", out var reasoningField))
        {
            // Object = model hỗ trợ reasoning (shape 2026-10); boolean theo shape cũ; giá trị lạ = không phát biểu.
            switch (reasoningField.ValueKind)
            {
                case JsonValueKind.Object:
                    reasoning = true;
                    efforts = SupportedEffortsOf(reasoningField);
                    break;
                case JsonValueKind.True:
                    reasoning = true;
                    break;
                case JsonValueKind.False:
                    reasoning = false;
                    break;
            }
        }

        return new FreeModelInfo(
            id,
            DisplayNameOf(item),
            ContextWindowOf(item),
            SupportsTools: tools,
            SupportsThink: reasoning,
            ThinkEfforts: efforts,
            InputModalities: RouterModalitiesOf(item, "input_modalities"),
            OutputModalities: RouterModalitiesOf(item, "output_modalities"));
    }

    /// <summary>
    /// reasoning.supported_efforts (R-T5-4) → danh sách mức effort model hỗ trợ; key vắng hoặc
    /// không phải mảng string → null (= không phát biểu effort nào, giữ đúng "không bịa").
    /// </summary>
    private static IReadOnlyList<string>? SupportedEffortsOf(JsonElement reasoningObject)
    {
        if (!reasoningObject.TryGetProperty("supported_efforts", out var efforts)
            || efforts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var value in efforts.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String) result.Add(value.GetString()!);
        }
        return result;
    }

    /// <summary>
    /// architecture.input_modalities/output_modalities → danh sách token; thiếu/không phải array → null
    /// (= không phát biểu gì). Array rỗng trả list rỗng — với nguồn full-list đó là phát biểu
    /// "không modality nào", khác hẳn vắng field.
    /// </summary>
    private static IReadOnlyList<string>? RouterModalitiesOf(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty("architecture", out var architecture)
            || architecture.ValueKind != JsonValueKind.Object
            || !architecture.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String) result.Add(value.GetString()!);
        }
        return result;
    }

    private static bool IsZero(JsonElement element) => element.ValueKind switch
    {
        // OpenRouter trả string ("0", "0.0", "0.0000025"); number phòng khi API đổi shape
        JsonValueKind.String =>
            decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value == 0m,
        JsonValueKind.Number => element.GetDecimal() == 0m,
        _ => false,
    };

    private static string? DisplayNameOf(JsonElement item) =>
        item.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(name.GetString())
            ? name.GetString()
            : null;

    private static int? ContextWindowOf(JsonElement item) =>
        item.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number
            ? ctx.GetInt32()
            : null;
}
