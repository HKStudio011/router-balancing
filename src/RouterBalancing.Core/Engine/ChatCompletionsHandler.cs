using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phần handler của pipeline queue-first (3B): <see cref="PrepareAsync"/> (buffer + validate —
/// endpoint gọi trước enqueue) và <see cref="ForwardAsync"/> (key → upstream → stream —
/// dispatcher gọi khi đã giữ slot). Singleton, không giữ state per-request.
/// </summary>
public sealed class ChatCompletionsHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log)
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
            log.Warn($"Yêu cầu chat không hợp lệ: {validation.Failure}.", LogCategory.Request);
            await WriteErrorAsync(ctx, 400, message, "invalid_request_error", param, null);
            return null;
        }

        return new PreparedChatRequest(validation.ModelId!, body);
    }

    /// <summary>
    /// Forward request đã resolve lên upstream. 2xx → stream + <see cref="DispatchOutcome.Handled"/>;
    /// lỗi retryable (429/408/5xx/network) → buffer + <see cref="DispatchOutcome.Retryable"/>
    /// (KHÔNG ghi response — dispatcher walk, spec §2.2); 4xx còn lại →
    /// <see cref="DispatchOutcome.Passthrough"/> (endpoint ghi, quen sát 3A); no-key → Error(503);
    /// client abort propagate.
    /// </summary>
    /// <param name="ctx">HttpContext gốc (ghi status/content-type/stream khi 2xx).</param>
    /// <param name="provider">Provider đã chọn.</param>
    /// <param name="model">Model đã chọn (log Info).</param>
    /// <param name="body">Body JSON gốc.</param>
    /// <param name="ct">Token — dùng <c>ctx.RequestAborted</c> để disconnect cắt stream.</param>
    public async Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
        byte[] body, CancellationToken ct)
    {
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, protector);
        if (key is null)
        {
            log.Warn($"Provider '{provider.Name}' không có account enabled nào.", LogCategory.Request);
            return new DispatchOutcome.Error(503,
                $"No enabled API key for provider '{provider.Name}'", "server_error", null, null);
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await upstream.PostChatCompletionAsync(provider, key, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Lỗi upstream thật → retryable Status null (502 sinh ở exhaustion — T5);
            // client tự ngắt (RequestAborted) thì propagate (hành vi 3A)
            log.Error($"Không kết nối được upstream '{provider.Name}'.", ex, LogCategory.Request);
            return new DispatchOutcome.Retryable(null, null, [], null);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                // 2xx giữ nguyên 3A/3B: stream thẳng, không buffer
                ctx.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } okType)
                    ctx.Response.ContentType = okType.ToString();
                await response.Content.CopyToAsync(ctx.Response.Body, ct);
                LogForwarded(provider, model, response, stopwatch);
                return new DispatchOutcome.Handled();
            }

            // Lỗi chưa commit (vừa nhận header) — buffer để dispatcher quyết định advance/passthrough
            var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var retryAfterRaw = response.Headers.RetryAfter?.ToString();
            if (RetryClassifier.IsRetryable(response.StatusCode))
                return new DispatchOutcome.Retryable((int)response.StatusCode, contentType,
                    errorBody, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));

            LogForwarded(provider, model, response, stopwatch);
            return new DispatchOutcome.Passthrough((int)response.StatusCode, contentType,
                errorBody, retryAfterRaw);
        }
    }

    // Tách helper để 2 nhánh (2xx/passthrough) ghi Info đúng 1 lần, không trùng chữ ký log
    private void LogForwarded(Provider provider, Model model, HttpResponseMessage response,
        Stopwatch stopwatch) =>
        log.Info(
            $"Chuyển tiếp '{model.ModelId}' → '{provider.Name}': " +
            $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
            LogCategory.Request);

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
