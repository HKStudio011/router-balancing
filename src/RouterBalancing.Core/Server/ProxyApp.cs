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

        // Queue-first 3B (spec §2.1): endpoint chỉ enqueue + chờ outcome;
        // DispatcherLoop (hosted service) resolve → chọn → serve.
        builder.Services.AddSingleton<IRequestQueue, RequestQueue>();
        builder.Services.AddSingleton<IExecutionList, ExecutionList>();
        builder.Services.AddSingleton<IComboResolver, ComboResolver>();
        builder.Services.AddSingleton<IModelSelector, ModelSelector>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
        builder.Services.AddHostedService<DispatcherLoop>();
    }

    /// <summary>
    /// Gắn pipeline: auth middleware + endpoint. Gọi NGAY SAU <c>Build()</c>, TRƯỚC <c>StartAsync</c>.
    /// </summary>
    /// <param name="app">WebApplication vừa Build.</param>
    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ApiKeyMiddleware>();

        // Queue-first 3B (spec §2.1): validate → enqueue → chờ dispatcher → ghi response theo outcome
        app.MapPost("/v1/chat/completions",
            async (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ChatCompletionsHandler handler, ILogService log) =>
        {
            // Sinh id TRƯỚC prepare — trả X-Request-Id để client đối chiếu với GET /v1/requests (spec §3.6)
            string id;
            do
            {
                id = RequestId.New();
            }
            while (queue.Contains(id) || executions.Contains(id));
            ctx.Response.Headers["X-Request-Id"] = id;

            var prepared = await handler.PrepareAsync(ctx);
            if (prepared is null)
                return; // validate fail — PrepareAsync đã ghi 400, chưa enqueue

            var priority = RequestPriorityParser.Parse(ctx.Request.Headers["X-Priority"].ToString());
            var request = new ProxyRequest(id, priority, prepared.ModelId, prepared.Body, ctx);

            if (!queue.Enqueue(request))
            {
                // id vừa sinh nên gần như không xảy ra — không được nuốt im lặng
                log.Warn($"Không enqueue được request {id}.", LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                    "server_error", null, null);
                return;
            }

            // Đăng ký SAU Enqueue: dispatcher đã Take thì TryRemove false → serve tự cắt stream (spec §3.4)
            ctx.RequestAborted.Register(() =>
            {
                if (queue.TryRemove(id, out var removed))
                {
                    log.Info($"Request {id} bị client ngắt khi đang chờ.", LogCategory.Request);
                    removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
                }
            });

            var outcome = await request.Completion.Task;

            if (outcome is DispatchOutcome.Error error)
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, error.Status, error.Message,
                    error.Type, error.Param, error.Code);
            }
            else if (outcome is DispatchOutcome.Cancelled
                     && !ctx.RequestAborted.IsCancellationRequested)
            {
                // Huỷ qua control API (client còn kết nối) — ghi log + 400 request_cancelled (spec §5)
                log.Info($"Đã huỷ request {id} (đang chờ), model {prepared.ModelId}.",
                    LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 400, "Request cancelled.",
                    "invalid_request_error", null, "request_cancelled");
            }
            // Handled / Aborted / Cancelled do client ngắt: response đã ghi hoặc kết nối đã đóng
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
