using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using System.Text.Json;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Orchestrator cho POST /v1/chat/completions: validate → resolve → key → upstream → stream về.
/// Singleton, không giữ state per-request — mọi trạng thái qua <see cref="HttpContext"/>.
/// </summary>
public sealed class ChatCompletionsHandler(
    IModelResolver resolver,
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log)
{
    /// <summary>
    /// Xử lý 1 request chat: trả response (thành công stream hoặc error JSON §4) và đúng 1 dòng log.
    /// </summary>
    /// <param name="ctx">HttpContext của request hiện tại.</param>
    public async Task HandleAsync(HttpContext ctx)
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
            return;
        }

        var modelId = validation.ModelId!;
        var resolved = await resolver.ResolveAsync(modelId, ct);
        if (resolved is ModelResolveFailure failure)
        {
            if (failure.Reason == ResolveFailure.NotFound)
            {
                log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
                await WriteErrorAsync(ctx, 404, $"The model '{failure.ModelId}' does not exist",
                    "invalid_request_error", "model", "model_not_found");
            }
            else
            {
                log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                    LogCategory.Request);
                await WriteErrorAsync(ctx, 503, $"Model '{failure.ModelId}' is not supported yet",
                    "server_error", null, null);
            }

            return;
        }

        var success = (ModelResolveSuccess)resolved;
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(success.Provider, protector);
        if (key is null)
        {
            log.Warn($"Provider '{success.Provider.Name}' không có account enabled nào.",
                LogCategory.Request);
            await WriteErrorAsync(ctx, 503,
                $"No enabled API key for provider '{success.Provider.Name}'",
                "server_error", null, null);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await upstream.PostChatCompletionAsync(success.Provider, key, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Client tự ngắt (RequestAborted) thì để propagate — không phải lỗi upstream
            log.Error($"Không kết nối được upstream '{success.Provider.Name}'.", ex, LogCategory.Request);
            await WriteErrorAsync(ctx, 502, "Upstream provider request failed", "server_error", null, null);
            return;
        }

        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();

            await response.Content.CopyToAsync(ctx.Response.Body, ct);
            log.Info(
                $"Chuyển tiếp '{success.Model.ModelId}' → '{success.Provider.Name}': " +
                $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
                LogCategory.Request);
        }
    }

    // Encoder relax để giữ nguyên ' (ASCII apostrophe) trong message —
    // default encoder escape thành \u0027 làm sai contract OpenAI (spec §4).
    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string message,
        string type, string? param, string? code)
    {
        ctx.Response.StatusCode = status;
        // Serialize trực tiếp (không WriteAsJsonAsync) để ContentType đúng như middleware: application/json
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { error = new { message, type, param, code } }, ErrorJsonOptions);
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }
}
