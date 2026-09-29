using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Cấu hình WebApplication cho proxy — tách khỏi ProxyHost để integration test
/// dựng cùng pipeline qua TestServer (spec 3A §2.1), tránh 2 nơi diverge.
/// Tách 2 method vì middleware/map phải chạy sau Build(): DI đóng băng sau Build.
/// </summary>
public static class ProxyApp
{
    /// <summary>
    /// Đăng ký DI cho proxy. Phải gọi TRƯỚC <c>builder.Build()</c>.
    /// </summary>
    /// <param name="builder">Builder do ProxyHost (hoặc test) tạo.</param>
    /// <param name="protector">Giải mã API key provider — đăng ký singleton dùng chung.</param>
    public static void ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector)
    {
        builder.Services.AddSingleton(protector);

        // Streaming SSE vô hạn — timeout (mặc định 100s) cắt giữa chừng là mất stream;
        // fail kết nối do ConnectTimeout để không treo vô hạn khi upstream chết.
        builder.Services.AddHttpClient(OpenAiUpstreamClient.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        builder.Services.AddSingleton<IModelResolver, ModelResolver>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
    }

    /// <summary>
    /// Gắn pipeline: auth middleware + endpoint. Gọi NGAY SAU <c>Build()</c>, TRƯỚC <c>StartAsync</c>.
    /// </summary>
    /// <param name="app">WebApplication vừa Build.</param>
    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ApiKeyMiddleware>();

        // Interim 3B (T5): validate → resolve (3A) → forward. T7 thay bằng queue-first.
        app.MapPost("/v1/chat/completions",
            async (HttpContext ctx, IModelResolver resolver, ILogService log,
                ChatCompletionsHandler handler) =>
        {
            var prepared = await handler.PrepareAsync(ctx);
            if (prepared is null)
                return;

            var resolved = await resolver.ResolveAsync(prepared.ModelId, ctx.RequestAborted);
            if (resolved is ModelResolveFailure failure)
            {
                if (failure.Reason == ResolveFailure.NotFound)
                {
                    log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
                    await ChatCompletionsHandler.WriteErrorAsync(ctx, 404,
                        $"The model '{failure.ModelId}' does not exist",
                        "invalid_request_error", "model", "model_not_found");
                }
                else
                {
                    log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                        LogCategory.Request);
                    await ChatCompletionsHandler.WriteErrorAsync(ctx, 503,
                        $"The model '{failure.ModelId}' is not supported yet", "server_error", null, null);
                }

                return;
            }

            var success = (ModelResolveSuccess)resolved;
            var outcome = await handler.ForwardAsync(ctx, success.Provider, success.Model,
                prepared.Body, ctx.RequestAborted);
            if (outcome is DispatchOutcome.Error error)
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, error.Status, error.Message,
                    error.Type, error.Param, error.Code);
            }
        });

        MapEndpoints(app);
    }

    private static void MapEndpoints(WebApplication app)
    {
        // /health mở luôn (middleware bỏ qua path này) — watchdog của Phase 2 dùng để ping
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        // Danh sách model đã bật, đúng shape OpenAI /v1/models để client không cần phân biệt
        app.MapGet("/v1/models", async (HttpContext http) =>
        {
            var factory = http.RequestServices
                .GetRequiredService<IDbContextFactory<RouterBalancingDbContext>>();
            using var db = factory.CreateDbContext();
            var models = await db.Models.AsNoTracking()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Id)
                .Select(m => new
                {
                    id = m.ModelId,
                    // @object: keyword 'object' không đặt được thẳng làm tên member — JSON vẫn ra "object"
                    @object = "model",
                    created = m.CreatedAt.ToUnixTimeSeconds(),
                    owned_by = m.Provider != null ? m.Provider.Name : "unknown",
                })
                .ToListAsync(http.RequestAborted);

            return Results.Json(new { @object = "list", data = models });
        });
    }
}
