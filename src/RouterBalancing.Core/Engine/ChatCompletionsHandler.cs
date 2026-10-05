using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phần handler của pipeline queue-first (3B): <see cref="PrepareAsync"/> (buffer + validate —
/// endpoint gọi trước enqueue) và <see cref="ForwardAsync"/> (key → upstream → stream —
/// dispatcher gọi khi đã giữ slot). Singleton, không giữ state per-request.
/// </summary>
public sealed class ChatCompletionsHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log,
    IClientKeyUsageSink usageSink)
{
    /// <summary>Body đã buffer + model id đã validate — input cho enqueue.</summary>
    public sealed record PreparedChatRequest(string ModelId, byte[] Body);

    /// <summary>
    /// Buffer body rồi validate theo rule 3A. Lỗi validate → ghi 400 OpenAI-style NGAY
    /// (không qua queue, header <c>X-Request-Id</c> đã được endpoint set trước đó) và trả
    /// <see langword="null"/>; hợp lệ → trả prepared request.
    /// </summary>
    /// <param name="ctx">HttpContext của request gốc.</param>
    public async Task<PreparedChatRequest?> PrepareAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var validation = ChatRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            var (message, param) = validation.Failure switch
            {
                ValidationFailure.MissingModel =>
                    ("Missing required parameter: 'model'.", "model"),
                ValidationFailure.MissingMessages =>
                    ("Missing required parameter: 'messages'.", "messages"),
                _ => ("Invalid JSON body", null),
            };
            // Spec §7: row validate-fail vẫn phải correlate được request — middleware đã set
            // Items vào ctx nên gắn RequestId/ClientKeyId như các row Write khác; Message giữ nguyên.
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategory.Request,
                Message = $"Yêu cầu chat không hợp lệ: {validation.Failure}.",
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
            await WriteErrorAsync(ctx, 400, message, "invalid_request_error", param, null);
            return null;
        }

        return new PreparedChatRequest(validation.ModelId!, body);
    }

    /// <summary>
    /// Forward request đã resolve lên upstream. 2xx → stream + <see cref="DispatchOutcome.Handled"/>;
    /// lỗi retryable (429/408/5xx) → buffer + <see cref="DispatchOutcome.Retryable"/>
    /// (KHÔNG ghi response — dispatcher walk, spec §2.2); lỗi fatal (401/403/404/mạng) →
    /// <see cref="DispatchOutcome.Fatal"/> (dispatcher advance đúng cấp failover,
    /// spec exhaustive-failover §3.1); 4xx còn lại → <see cref="DispatchOutcome.Passthrough"/>
    /// (endpoint ghi, quen sát 3A); no-key → Error(503); client abort propagate.
    /// </summary>
    /// <param name="ctx">HttpContext gốc (ghi status/content-type/stream khi 2xx).</param>
    /// <param name="provider">Provider đã chọn.</param>
    /// <param name="model">Model đã chọn (log Info).</param>
    /// <param name="body">Body JSON gốc.</param>
    /// <param name="accountId">TK đã chọn lúc TryEnter — dùng đúng id này, không resolve lại (D-B6).</param>
    /// <param name="ct">Token — dùng <c>ctx.RequestAborted</c> để disconnect cắt stream.</param>
    public async Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
        byte[] body, long accountId, CancellationToken ct)
    {
        // Đúng TK đã chọn bởi TryEnter — resolve "first enabled" tại đây sẽ lệch đếm (D-B6);
        // id không khớp (TK bị xóa giữa chừng) → 503 cùng contract với nhánh không có key
        var account = provider.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
        {
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategory.Request,
                Message = $"Provider '{provider.Name}' không có account enabled nào.",
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
            return new DispatchOutcome.Error(503,
                $"No enabled API key for provider '{provider.Name}'", "server_error", null, null);
        }

        // No-key account: cột key rỗng → gửi "" (upstream không auth) — Unprotect("") ném
        // CryptographicException nên phải rẽ nhánh tường minh (spec free-account D7)
        var key = string.IsNullOrEmpty(account.ApiKeyEncrypted)
            ? string.Empty
            : protector.Unprotect(account.ApiKeyEncrypted);

        // Đặt context proxy TRƯỚC khi gọi handler (upstream.PostChatCompletionAsync) —
        // ProxyHealthHandler đọc ProxyTarget.Current để dispatch theo assignment.
        ProxyTarget.Current.Value = new ProxyTarget(provider, account);
        try
        {
            // Alias nội bộ (pin/combo) phải đổi về model id thật trước khi forward —
            // upstream không biết pin/ten combo (bug client: gửi nguyên alias bị ModelError).
            var upstreamBody = ChatBody.WithModel(body, model.ModelId);
            // Yêu cầu upstream trả usage cho stream OpenAI (spec §6) — body gốc giữ nguyên ở queue/prepare
            var (requestBody, expectsUsage) = UsageCapture.WithIncludeUsage(upstreamBody, provider.Type);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await upstream.PostChatCompletionAsync(provider, key, requestBody, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !ctx.RequestAborted.IsCancellationRequested)
            {
                // Lỗi upstream thật → Fatal(Provider, Status null) — dispatcher advance
                // (spec exhaustive-failover §3.1); client tự ngắt (RequestAborted) thì propagate (hành vi 3A)
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Error,
                    Category = LogCategory.Request,
                    Message = $"Không kết nối được upstream '{provider.Name}'.",
                    Details = ex.ToString(),
                    ErrorCode = ex.GetType().Name,
                    RequestId = ClientKeyItems.RequestIdOf(ctx),
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
                return new DispatchOutcome.Fatal(FailoverLevel.Provider, null, null, [], null);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    // 2xx giữ nguyên 3A/3B: stream thẳng — tee quét usage trong lúc copy (spec §6.1)
                    ctx.Response.StatusCode = (int)response.StatusCode;
                    if (response.Content.Headers.ContentType is { } okType)
                        ctx.Response.ContentType = okType.ToString();
                    var usage = await UsageCapture.TeeAsync(response.Content, ctx.Response.Body, ct);
                    LogForwarded(ctx, provider, model, response, stopwatch);

                    if (usage is not null)
                    {
                        log.LogRequestUsage(ClientKeyItems.RequestIdOf(ctx), ClientKeyItems.IdOf(ctx),
                            usage.PromptTokens, usage.CompletionTokens);
                        // Fail-open nằm trong sink: DB lỗi → log Error, không nổ sau khi đã stream (spec §10)
                        await usageSink.RecordAsync(ClientKeyItems.IdOf(ctx),
                            usage.PromptTokens, usage.CompletionTokens, ct);
                    }
                    else if (expectsUsage)
                    {
                        // Đã yêu cầu include_usage mà không có usage — telemetry bất thường, không fail request;
                        // gắn id để correlate row Debug với request khi upstream thiếu usage (spec §7)
                        log.Write(new LogEntry
                        {
                            Severity = LogSeverity.Debug,
                            Category = LogCategory.App,
                            Message = "Upstream không trả usage dù đã yêu cầu include_usage — counter token không tăng.",
                            RequestId = ClientKeyItems.RequestIdOf(ctx),
                            ClientKeyId = ClientKeyItems.IdOf(ctx),
                        });
                    }
                    return new DispatchOutcome.Handled();
                }

                // Lỗi chưa commit (vừa nhận header) — buffer để dispatcher quyết định advance/passthrough
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                var contentType = response.Content.Headers.ContentType?.ToString();
                var retryAfterRaw = response.Headers.RetryAfter?.ToString();
                if (RetryClassifier.IsRetryable(response.StatusCode))
                    return new DispatchOutcome.Retryable((int)response.StatusCode, contentType,
                        errorBody, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));

                // Journal Info chung cho Fatal lẫn Passthrough (parity quen sát 3A)
                LogForwarded(ctx, provider, model, response, stopwatch);
                return ClassifyFatal((int)response.StatusCode, errorBody, contentType,
                    retryAfterRaw, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));
            }
            }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }

    /// <summary>
    /// Phân loại lỗi non-retryable (spec exhaustive-failover §3.1): 401/403 → account (auth nằm ở
    /// account — §1.3 #6); 404 có <c>error.code=model_not_found</c> → model; 404 còn lại →
    /// provider; status khác (400/409/422...) → Passthrough giữ nguyên hành vi 3A.
    /// Payload giữ nguyên cho cả Fatal — exhaustion passthrough nguyên response cuối (§4).
    /// </summary>
    private static DispatchOutcome ClassifyFatal(int status, byte[] body, string? contentType,
        string? retryAfterRaw, TimeSpan? retryAfter)
    {
        if (status is 401 or 403)
        {
            return new DispatchOutcome.Fatal(FailoverLevel.Account, status, contentType, body,
                retryAfter);
        }

        if (status == 404)
        {
            return IsModelNotFound(body)
                ? new DispatchOutcome.Fatal(FailoverLevel.Model, status, contentType, body, retryAfter)
                : new DispatchOutcome.Fatal(FailoverLevel.Provider, status, contentType, body, retryAfter);
        }

        return new DispatchOutcome.Passthrough(status, contentType, body, retryAfterRaw);
    }

    /// <summary>
    /// Nhận diện <c>error.code == "model_not_found"</c> best-effort (V2): body hỏng/không phải
    /// JSON → coi 404 thường (Fatal cấp Provider); so sánh ordinal-ignore-case.
    /// </summary>
    // Nhận byte[] chứ không ReadOnlySpan: JsonDocument.Parse chỉ có overload
    // string/ReadOnlyMemory/ReadOnlySequence — không có overload span để bind
    private static bool IsModelNotFound(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "model_not_found",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Tách helper để 2 nhánh (2xx/passthrough) ghi Info đúng 1 lần, không trùng chữ ký log;
    // convert sang Write để gắn RequestId/ClientKeyId (spec §7) — Message giữ nguyên
    private void LogForwarded(HttpContext ctx, Provider provider, Model model,
        HttpResponseMessage response, Stopwatch stopwatch) =>
        log.Write(new LogEntry
        {
            Severity = LogSeverity.Info,
            Category = LogCategory.Request,
            Message =
                $"Chuyển tiếp '{model.ModelId}' → '{provider.Name}': " +
                $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
            RequestId = ClientKeyItems.RequestIdOf(ctx),
            ClientKeyId = ClientKeyItems.IdOf(ctx),
        });

    // Encoder relax giữ nguyên apostrophe (0x27) trong message — default encoder escape
    // apostrophe thành chuỗi unicode, sai contract OpenAI (spec §4) — GIỮ NGUYÊN comment 3A
    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ghi JSON lỗi OpenAI-style — tái dùng cho endpoint (validate, resolve, cancel).</summary>
    internal static async Task WriteErrorAsync(HttpContext ctx, int status, string message,
        string type, string? param, string? code)
    {
        // Response đã commit (stream SSE giữa chừng) — không được append JSON lỗi vào stream
        if (ctx.Response.HasStarted)
            return;

        ctx.Response.StatusCode = status;
        // Serialize trực tiếp (không WriteAsJsonAsync) để ContentType đúng như middleware: application/json
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { error = new { message, type, param, code } }, ErrorJsonOptions);
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }
}
