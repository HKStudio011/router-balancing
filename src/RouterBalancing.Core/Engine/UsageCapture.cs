using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Đọc usage token từ upstream response (spec client-keys §6):
/// (1) <see cref="WithIncludeUsage"/> chèn <c>stream_options.include_usage=true</c> cho stream OpenAI;
/// (2) <see cref="TeeAsync"/> copy body sang client MÀ không nuốt/chỉnh sửa byte — vừa forward vừa quét usage.
/// </summary>
internal static class UsageCapture
{
    // Cap ResponseBody monitor = 64KB — không cap thì row DB phình theo upstream (spec api-monitor §2)
    private const int BodyCapChars = 64 * 1024;
    private const string TruncatedMarker = "[truncated]";

    /// <summary>Usage đã đọc được từ response (OpenAI-shape prompt_tokens/completion_tokens).</summary>
    public sealed record Usage(int PromptTokens, int CompletionTokens);

    /// <summary>
    /// Kết quả tee: usage đã parse + metadata cho monitor panel (spec api-monitor §3.3).
    /// </summary>
    /// <param name="Usage">Usage đọc được; <see langword="null"/> nếu không có hoặc JSON hỏng.</param>
    /// <param name="FirstTokenAt">Thời điểm byte upstream đầu tiên về — chỉ SSE;
    /// non-stream luôn <see langword="null"/> (không có khái niệm token đầu).</param>
    /// <param name="ResponseBody">Thân response đã buffer cho monitor: SSE ghép payload dòng
    /// <c>data:</c> (bỏ <c>[DONE]</c>), non-stream là toàn thân; đã cap <c>64 * 1024</c> ký tự
    /// + marker <c>[truncated]</c> nếu dài hơn.</param>
    public sealed record TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody);

    /// <summary>
    /// Trả body upstream (inject <c>stream_options.include_usage</c> nếu đủ điều kiện) + cờ
    /// <c>ExpectsUsage</c> (true = đã yêu cầu usage → thiếu là bất thường, caller log Debug).
    /// </summary>
    public static (byte[] Body, bool ExpectsUsage) WithIncludeUsage(byte[] body, ProviderType providerType)
    {
        // Chỉ upstream OpenAI-compatible hiểu stream_options; Anthropic contract khác (spec §6)
        if (providerType != ProviderType.OpenAI) return (body, false);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            // Body không phải JSON hợp lệ — để validator/upstream xử lý, không đụng vào
            return (body, false);
        }

        if (node is not JsonObject obj) return (body, false);

        var isStream = obj.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var flag)
            && flag;
        if (!isStream) return (body, false);

        var options = obj.TryGetPropertyValue("stream_options", out var optionsNode)
            && optionsNode is JsonObject existing
            ? existing
            : new JsonObject();
        options["include_usage"] = true;
        obj["stream_options"] = options;
        // ToJsonString escape unicode nhưng JSON escape vẫn semantic-equivalent — upstream parse y hệt
        return (Encoding.UTF8.GetBytes(obj.ToJsonString()), true);
    }

    /// <summary>
    /// Forward toàn bộ response sang <paramref name="dest"/> và trả <see cref="TeeResult"/> —
    /// byte-forward cho client giữ nguyên tuyệt đối (ghi nguyên chunk như đọc được, không sửa).
    /// SSE → ghi từng chunk ngay, scan dòng theo byte (dòng có thể cắt giữa 2 chunk);
    /// loại khác (JSON non-stream) → buffer, parse, rồi ghi tiếp — spec §6.1.
    /// </summary>
    public static async Task<TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct)
    {
        var stream = await source.ReadAsStreamAsync(ct);
        var mediaType = source.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await TeeSseAsync(stream, dest, ct);

        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, ct);
        var payload = bytes.ToArray();
        await dest.WriteAsync(payload, ct);
        var body = Encoding.UTF8.GetString(payload);
        if (body.Length > BodyCapChars)
            body = body[..BodyCapChars] + TruncatedMarker;
        return new TeeResult(ParseUsage(payload), null, body);
    }

    internal static async Task<TeeResult> TeeSseAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        byte[] carry = [];
        Usage? usage = null;
        var body = new StringBuilder();
        var bodyTruncated = false;
        DateTimeOffset? firstTokenAt = null;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                // TTFT phải tính tại thời điểm byte đầu tiên về — đặt ngay khi read > 0 (spec api-monitor §5)
                firstTokenAt ??= DateTimeOffset.UtcNow;
                // Ghi NGUYÊN chunk cho client TRƯỚC khi scan — byte-forward không đổi (spec §6.1)
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);

                var combined = new byte[carry.Length + read];
                Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
                Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);

                var lineStart = 0;
                for (var i = 0; i < combined.Length; i++)
                {
                    if (combined[i] != (byte)'\n') continue;
                    var line = combined.AsSpan(lineStart, i - lineStart);
                    usage = ScanSseLine(line, usage);
                    AppendSsePayload(line, body, ref bodyTruncated);
                    lineStart = i + 1;
                }
                carry = combined[lineStart..];
            }
            // Dòng cuối không có \n (SSE chuẩn luôn có nhưng không assume) — xử lý nốt
            if (carry.Length > 0)
            {
                usage = ScanSseLine(carry, usage);
                AppendSsePayload(carry, body, ref bodyTruncated);
            }
            return new TeeResult(usage, firstTokenAt, body.ToString());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Tích payload dòng "data:" (bỏ 1 space sau ':' theo SSE spec, bỏ [DONE]) làm ResponseBody cho monitor.
    // Vượt cap thì cắt + gắn marker đúng 1 lần rồi ngừng tích — body cuối luôn ≤ cap, không phình vô hạn
    // dù upstream gửi một dòng khổng lồ.
    private static void AppendSsePayload(ReadOnlySpan<byte> line, StringBuilder body, ref bool truncated)
    {
        if (truncated || !line.StartsWith("data:"u8)) return;
        var payload = line["data:"u8.Length..];
        if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];
        if (payload.StartsWith("[DONE]"u8)) return;
        if (body.Length > 0) body.Append('\n');
        body.Append(Encoding.UTF8.GetString(payload));
        if (body.Length <= BodyCapChars) return;
        // Marker SSE nối bằng "\n" nên content cắt tại cap-1 → tổng = 64*1024 + "[truncated]" (cap global)
        body.Length = BodyCapChars - 1;
        body.Append('\n').Append(TruncatedMarker);
        truncated = true;
    }

    // Chỉ dòng "data: {...}" chứa usage hợp lệ; JSON hỏng → bỏ qua dòng đó, không nổ stream.
    // Guard ValueKind Number trước GetInt32: field sai kiểu (string/bool) ném InvalidOperationException
    // (KHÔNG phải JsonException) — spec §10 yêu cầu usage parse lỗi không được throw làm hỏng request.
    private static Usage? ScanSseLine(ReadOnlySpan<byte> line, Usage? current)
    {
        if (!line.StartsWith("data:"u8)) return current;
        var payload = line["data:"u8.Length..];
        if (payload.StartsWith("[DONE]"u8)) return current;
        try
        {
            // net10 không còn overload Parse(ReadOnlySpan<byte>) — ToArray() sang ReadOnlyMemory<byte>
            using var doc = JsonDocument.Parse(payload.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return current;
            if (!doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object
                || !usage.TryGetProperty("prompt_tokens", out var prompt)
                || prompt.ValueKind != JsonValueKind.Number
                || !usage.TryGetProperty("completion_tokens", out var completion)
                || completion.ValueKind != JsonValueKind.Number)
                return current;
            // last-wins: nhiều chunk usage → lấy cái cuối (spec §6.1)
            return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (JsonException)
        {
            return current;
        }
    }

    private static Usage? ParseUsage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var prompt)
                && prompt.ValueKind == JsonValueKind.Number
                && usage.TryGetProperty("completion_tokens", out var completion)
                && completion.ValueKind == JsonValueKind.Number)
                return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (JsonException)
        {
            // Body không parse được → không có usage, request vẫn thành công (spec §10)
        }
        return null;
    }
}
