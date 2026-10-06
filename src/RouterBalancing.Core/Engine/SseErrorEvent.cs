using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Builder event lỗi in-band cho client đang đọc stream SSE — mỗi method trả về
/// đúng một frame <c>data: {"type":"error","error":{...}}\n\n</c> (một dòng data,
/// LF, không <c>[DONE]</c> — spec early-headers §3.5).
/// </summary>
internal static class SseErrorEvent
{
    private const string DataPrefix = "data: ";

    // Encoder relax giữ nguyên apostrophe (0x27) — default encoder escape thành
    // \u0027, sai contract OpenAI (cùng lý do ChatCompletionsHandler.ErrorJsonOptions).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Lỗi đã map từ <c>DispatchOutcome.Error</c> — envelope
    /// <c>{"type":"error","error":{message,type,param,code,status}}</c>;
    /// <paramref name="param"/>/<paramref name="code"/> null serialize thành <c>null</c>
    /// (parity <c>WriteErrorAsync</c>).
    /// </summary>
    internal static byte[] Error(int status, string message, string type, string? param, string? code) =>
        Frame(new { type = "error", error = new { message, type, param, code, status } });

    /// <summary>
    /// Request bị huỷ qua API/nút popup — shape tối giản spec §3.5:
    /// <c>{"type":"error","error":{"message":"Request cancelled.","type":"invalid_request_error","code":"request_cancelled"}}</c>
    /// (không <c>status</c>).
    /// </summary>
    internal static byte[] Cancelled() =>
        Frame(new
        {
            type = "error",
            error = new
            {
                message = "Request cancelled.",
                type = "invalid_request_error",
                code = "request_cancelled",
            },
        });

    /// <summary>
    /// Lỗi server trước khi có content (exception trước tee) — shape spec §3.5:
    /// <c>{"type":"error","error":{"message":"Internal server error","type":"server_error","code":"server_error"}}</c>
    /// (không <c>status</c>).
    /// </summary>
    internal static byte[] ServerFault() =>
        Frame(new
        {
            type = "error",
            error = new
            {
                message = "Internal server error",
                type = "server_error",
                code = "server_error",
            },
        });

    /// <summary>
    /// Đóng nguyên payload lỗi của upstream vào event:
    /// body JSON còn key <c>error</c> → giữ nguyên, gắn <c>type:"error"</c> và
    /// (khi có) <c>retry_after</c> (bù header <c>Retry-After</c> đã mất trên wire);
    /// ngược lại (không phải JSON / không có <c>error</c>) → fallback
    /// <c>{"type":"error","error":{message: DecodeCapped(body), type:"upstream_error", status, retry_after?}}</c>.
    /// </summary>
    /// <param name="status">HTTP status upstream trả về — gắn vào fallback.</param>
    /// <param name="body">Body lỗi thô của upstream (UTF-8, có thể nhiều MB).</param>
    /// <param name="retryAfterHeader">Giá trị header <c>Retry-After</c> thô; null → không thêm field.</param>
    internal static byte[] Passthrough(int status, byte[] body, string? retryAfterHeader)
    {
        JsonObject? root = null;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            // Body không phải JSON (HTML lỗi proxy...) → rơi xuống fallback bên dưới.
        }

        if (root is not null && root.ContainsKey("error"))
        {
            root["type"] = "error";
            if (retryAfterHeader is not null && root["error"] is JsonObject error)
                error["retry_after"] = retryAfterHeader;
            return Frame(root);
        }

        // DecodeCapped cắt byte trước decode (64KB) — body nhiều MB không bung string nguyên con
        var fallbackError = new JsonObject
        {
            ["message"] = ApiMonitorStore.DecodeCapped(body),
            ["type"] = "upstream_error",
            ["status"] = status,
        };
        if (retryAfterHeader is not null)
            fallbackError["retry_after"] = retryAfterHeader;

        return Frame(new JsonObject
        {
            ["type"] = "error",
            ["error"] = fallbackError,
        });
    }

    /// <summary>Bọc JSON thành frame SSE UTF-8: <c>data: {json}\n\n</c> (LF, đuôi trống 1 dòng).</summary>
    private static byte[] Frame<T>(T payload) =>
        Encoding.UTF8.GetBytes($"{DataPrefix}{JsonSerializer.Serialize(payload, JsonOptions)}\n\n");
}
