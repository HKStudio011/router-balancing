using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Chuẩn hoá body chat trước khi forward: alias nội bộ (pin <c>{identifier}/{modelId}</c>,
/// tên combo) chỉ dùng để chọn route — upstream chỉ hiểu model id thật.
/// </summary>
internal static class ChatBody
{
    /// <summary>
    /// Rewrite trường <c>model</c> thành <paramref name="modelId"/> đã resolve (bỏ identifier pin /
    /// tên combo). Body đã đúng model hoặc JSON hỏng → trả nguyên bytes (giữ hợp đồng byte-for-byte
    /// cho đường model id trực tiếp).
    /// </summary>
    public static byte[] WithModel(byte[] body, string modelId)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            // Validator đã chặn JSON hỏng trước enqueue — nhánh này chỉ để phòng thủ, không đụng body
            return body;
        }

        if (node is not JsonObject obj) return body;

        if (obj.TryGetPropertyValue("model", out var current)
            && current is JsonValue value
            && value.TryGetValue<string>(out var existing)
            && string.Equals(existing, modelId, StringComparison.Ordinal))
            return body;

        obj["model"] = modelId;
        // ToJsonString escape unicode nhưng JSON escape vẫn semantic-equivalent — upstream parse y hệt
        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }
}
