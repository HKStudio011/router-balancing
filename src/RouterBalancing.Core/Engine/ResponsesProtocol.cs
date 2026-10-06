using System.Text.Json;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Protocol <c>/v1/responses</c> — validate tối thiểu (model string không rỗng +
/// input string/array không rỗng, phần còn lại opaque), rewrite alias→model id thật,
/// <b>không</b> inject <c>stream_options.include_usage</c> vì Responses API luôn kèm
/// usage trong event terminal (spec v1-responses §4.1/§4.2).
/// </summary>
internal sealed class ResponsesProtocol : IProxyProtocol
{
    /// <inheritdoc/>
    public string UpstreamPath => "/v1/responses";

    /// <summary>
    /// Validate theo spec §4.1 — chỉ check presence/type của <c>model</c> và <c>input</c>;
    /// tools/reasoning/max_output_tokens... không kiểm, body forward nguyên byte.
    /// </summary>
    public ProxyValidationResult Validate(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Bắt cả body rỗng (Parse ném cho 0 byte) — coi như JSON hỏng, như chat V1
            return new ProxyValidationResult(false, null, false, "Invalid JSON body", null);
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Root không phải object (array/scalar/null) → không có trường model;
            // TryGetProperty ném trên non-object nên phải chặn ở đây (như chat V2).
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()))
                return new ProxyValidationResult(false, null, false,
                    "Missing required parameter: 'model'.", "model");

            // input hợp lệ = string không rỗng HOẶC array không rỗng;
            // null/số/bool/object và rỗng đều coi như thiếu (OpenAI trả 400 cùng message).
            var hasInput = root.TryGetProperty("input", out var input)
                && ((input.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(input.GetString()))
                    || (input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0));
            if (!hasInput)
                return new ProxyValidationResult(false, null, false,
                    "Missing required parameter: 'input'.", "input");

            // Chỉ literal true mới bật stream — thiếu/sai kiểu → non-stream (mặc định
            // an toàn: consumer chỉ flush header sớm khi chắc chắn request là SSE)
            var isStream = root.TryGetProperty("stream", out var stream)
                && stream.ValueKind == JsonValueKind.True;

            // ValueKind == String đã kiểm ở trên → GetString() không null (không cần !)
            return new ProxyValidationResult(true, model.GetString(), isStream, null, null);
        }
    }

    /// <inheritdoc/>
    public (byte[] Body, bool ExpectsUsage) PrepareUpstreamBody(byte[] body, string realModelId,
        ProviderType providerType)
    {
        // Chỉ đổi alias (pin/combo) về model id thật — không inject stream_options:
        // upstream /v1/responses không nhận field đó và usage đã có sẵn trong
        // response.completed (spec §4.2). providerType không cần cho rewrite này.
        return (ChatBody.WithModel(body, realModelId), false);
    }

    /// <inheritdoc/>
    public Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct) =>
        throw new NotImplementedException(
            "Tee cho /v1/responses sẽ được cài đặt ở Task 3 (spec v1-responses §4.3/§4.4).");
}
