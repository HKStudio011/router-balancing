using System.Globalization;
using System.Text.Json;

namespace RouterBalancing.Core.Providers;

/// <summary>Model free sau khi parse — DisplayName/ContextWindow lấy từ API nếu response có.</summary>
public sealed record FreeModelInfo(string ModelId, string? DisplayName, int? ContextWindow);

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
            result.Add(new FreeModelInfo(id!, DisplayNameOf(item), ContextWindowOf(item)));
        }
        return result;
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
