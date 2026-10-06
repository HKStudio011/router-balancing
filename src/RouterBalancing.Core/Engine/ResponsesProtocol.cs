using System.Buffers;
using System.Text;
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
    public async Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct)
    {
        // Dispatch theo content-type như chat: SSE → scan dòng event/data; còn lại coi là JSON non-stream
        // (spec §4.3/§4.4). Body không phải JSON ở nhánh sau → parse fail-open, byte vẫn forward đủ.
        var stream = await source.ReadAsStreamAsync(ct);
        var mediaType = source.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await TeeSseAsync(stream, dest, ct);

        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, ct);
        var payload = bytes.ToArray();
        await dest.WriteAsync(payload, ct);
        // DecodeCapped cắt BYTE trước khi decode + marker đúng 1 lần — semantics cap monitor dùng chung (spec §4.3)
        return new UsageCapture.TeeResult(ParseRootUsage(payload), null, ApiMonitorStore.DecodeCapped(payload));
    }

    // Cap ResponseBody monitor — phải khớp ApiMonitorStore.BodyCap (DecodeCapped cắt tại đây)
    private const int BodyCapBytes = 64 * 1024;

    private static async Task<UsageCapture.TeeResult> TeeSseAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        byte[] carry = [];   // dòng chưa kết thúc bằng \n — dòng SSE có thể cắt giữa 2 chunk (như chat)
        var capture = new MemoryStream(BodyCapBytes + 1);
        var state = new ResponsesSseState();
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                // Ghi NGUYÊN chunk cho client TRƯỚC khi parse — byte-forward không đổi (spec §4.4)
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                CaptureHead(capture, buffer, read);

                var combined = new byte[carry.Length + read];
                Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
                Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);

                var lineStart = 0;
                for (var i = 0; i < combined.Length; i++)
                {
                    if (combined[i] != (byte)'\n') continue;
                    state.HandleLine(combined.AsSpan(lineStart, i - lineStart));
                    lineStart = i + 1;
                }
                carry = combined[lineStart..];
            }
            // Dòng cuối không có \n (SSE chuẩn luôn có nhưng không assume) — xử lý nốt
            if (carry.Length > 0)
                state.HandleLine(carry);
            return new UsageCapture.TeeResult(state.Usage, state.FirstTokenAt,
                ApiMonitorStore.DecodeCapped(capture.ToArray()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Chỉ giữ 64KB+1 byte đầu: đủ để DecodeCapped cắt đúng cap và biết body dài hơn cap hay không
    // (byte dư thứ 65537 không decode, chỉ báo có marker) — memory không phình theo độ dài stream.
    private static void CaptureHead(MemoryStream capture, byte[] buffer, int count)
    {
        if (capture.Length > BodyCapBytes) return;
        var take = Math.Min(count, BodyCapBytes + 1 - (int)capture.Length);
        capture.Write(buffer, 0, take);
    }

    /// <summary>
    /// Máy trạng thái dòng SSE cho /v1/responses (spec §4.4): field <c>event:</c> đặt tên event
    /// (dòng trống = kết thúc event), field <c>data:</c> chỉ được parse cho event cần thiết
    /// (terminal để lấy usage). Comment keep-alive và event khác bỏ qua. JSON lỗi → bỏ event,
    /// không bao giờ throw (fail-open — protocol không có DI để log, pattern UsageCapture).
    /// </summary>
    private sealed class ResponsesSseState
    {
        private string? _eventName;

        public UsageCapture.Usage? Usage { get; private set; }
        public DateTimeOffset? FirstTokenAt { get; private set; }

        public void HandleLine(ReadOnlySpan<byte> line)
        {
            if (line.IsEmpty)
            {
                _eventName = null;
                return;
            }
            if (line[0] == (byte)':') return;   // comment / keep-alive

            if (line.StartsWith("event:"u8))
            {
                _eventName = DecodeFieldValue(line["event:"u8.Length..]);
                // TTFT = thời điểm event delta text ĐẦU TIÊN xuất hiện. .done cũng tính nhưng ??=
                // đảm bảo delta (đến trước trong stream hợp lệ) thắng — fallback chỉ khi chưa thấy delta.
                if (_eventName is "response.output_text.delta" or "response.output_text.done")
                    FirstTokenAt ??= DateTimeOffset.UtcNow;
                return;
            }
            if (!line.StartsWith("data:"u8)) return;   // id:/retry:… không liên quan

            var payload = line["data:"u8.Length..];
            if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];   // 1 space theo SSE spec
            if (_eventName is "response.completed" or "response.incomplete" or "response.failed")
                Usage = ParseTerminalUsage(payload) ?? Usage;
        }
    }

    private static string DecodeFieldValue(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).Trim();   // Trim bỏ \r của CRLF và space thừa

    // Event terminal: usage nằm trong object lồng `response.usage` (khác chat — usage ở root).
    private static UsageCapture.Usage? ParseTerminalUsage(ReadOnlySpan<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("response", out var response)
                || response.ValueKind != JsonValueKind.Object)
                return null;
            return ReadUsage(response);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Non-stream: usage ngay tại root (spec §4.3).
    private static UsageCapture.Usage? ParseRootUsage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadUsage(doc.RootElement) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // input_tokens→PromptTokens, output_tokens→CompletionTokens. Guard ValueKind.Number trước
    // GetInt32: field sai kiểu/decimals/out-of-range ném InvalidOperationException (KHÔNG phải
    // JsonException) — parse usage lỗi không được làm hỏng request (fail-open, spec §4.3).
    private static UsageCapture.Usage? ReadUsage(JsonElement holder)
    {
        if (!holder.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        if (!usage.TryGetProperty("input_tokens", out var input) || input.ValueKind != JsonValueKind.Number)
            return null;
        if (!usage.TryGetProperty("output_tokens", out var output) || output.ValueKind != JsonValueKind.Number)
            return null;
        return new UsageCapture.Usage(input.GetInt32(), output.GetInt32());
    }
}
