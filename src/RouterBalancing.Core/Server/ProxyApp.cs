using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
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
    /// <param name="pool">Pool proxy dùng chung — app truyền ProxyPool singleton, test truyền double.</param>
    public static void ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector, IProxyPool pool)
    {
        builder.Services.AddSingleton(protector);

        // Test container chỉ gọi ConfigureServices → pool instance (DirectProxyPool) vào;
        // app container có thể đã đăng ký ProxyPool trước → không ghi đè
        if (!builder.Services.Any(d => d.ServiceType == typeof(IProxyPool)))
        {
            builder.Services.AddSingleton(pool);
        }

        builder.Services.AddTransient<ProxyHealthHandler>();
        // Resolver singleton: dispatch theo provider+account (most-specific-wins, D1)
        builder.Services.AddSingleton<IProxySelectionResolver, ProxySelectionResolver>();

        // Streaming SSE vô hạn — timeout (mặc định 100s) cắt giữa chừng là mất stream;
        // fail kết nối do ConnectTimeout để không treo vô hạn khi upstream chết.
        // Proxy + health handler: outbound request qua pool (spec proxy-pool §4),
        // hết proxy sống thì handler tự attempt direct cuối.
        builder.Services.AddHttpClient(OpenAiUpstreamClient.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                Proxy = new RoundRobinWebProxy(),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            .AddHttpMessageHandler<ProxyHealthHandler>();

        // Queue-first 3B (spec §2.1): endpoint chỉ enqueue + chờ outcome;
        // DispatcherLoop (hosted service) resolve → chọn → serve.
        builder.Services.AddSingleton<IRequestQueue, RequestQueue>();
        builder.Services.AddSingleton<IExecutionList, ExecutionList>();
        builder.Services.AddSingleton<IComboResolver, ComboResolver>();
        builder.Services.AddSingleton<IModelSelector, ModelSelector>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
        // Circuit per-model 3C: store singleton + đồng hồ system —
        // DispatcherLoop (T5), gate endpoint (T6), watchdog (T7) dùng chung 1 instance
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IClientKeyRateLimiter, ClientKeyRateLimiter>();
        // Singleton (không factory): cache phải sống 1 lần/proxy container để event KeysChanged
        // attach đúng 1 lần; Dispose của container unsubscribe khi proxy dừng.
        builder.Services.AddSingleton<ClientKeyAuthCache>();
        builder.Services.AddSingleton<IClientKeyUsageSink, ClientKeyUsageSink>();
        builder.Services.AddSingleton<IModelHealthStore, ModelHealthStore>();
        builder.Services.AddHostedService<DispatcherLoop>();
        // Watchdog 3C (spec §2.1): singleton + hosted qua factory lấy ĐÚNG instance này —
        // integration test resolve ModelHealthWatchdog từ Services rồi gọi ProbeDueAsync
        builder.Services.AddSingleton<ModelHealthWatchdog>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelHealthWatchdog>());
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
                ChatCompletionsHandler handler, ILogService log, IModelHealthStore health) =>
        {
            // Sinh id TRƯỚC prepare — trả X-Request-Id để client đối chiếu với GET /v1/requests (spec §3.6)
            string id;
            do
            {
                id = RequestId.New();
            }
            while (queue.Contains(id) || executions.Contains(id));
            ctx.Response.Headers["X-Request-Id"] = id;
            ctx.Items[ClientKeyItems.RequestId] = id;

            var prepared = await handler.PrepareAsync(ctx);
            if (prepared is null)
                return; // validate fail — PrepareAsync đã ghi 400, chưa enqueue

            // Gate 3C (spec §3.4): exact-id đang ManualRetry → 503 §4 TRƯỚC khi vào queue.
            // Combo name chưa resolve lúc này — gate combo nằm ở walk (T5 filter bỏ candidate)
            if (health.IsManualRetry(prepared.ModelId))
            {
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Warning,
                    Category = LogCategory.Request,
                    Message = $"Từ chối request mới: model '{prepared.ModelId}' đang ManualRetry",
                    RequestId = id,
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 503,
                    $"The model '{prepared.ModelId}' is temporarily unavailable",
                    "server_error", null, null);
                return;
            }

            var priority = RequestPriorityParser.Parse(ctx.Request.Headers["X-Priority"].ToString());
            var request = new ProxyRequest(id, priority, prepared.ModelId, prepared.Body, ctx);

            if (!queue.Enqueue(request))
            {
                // id vừa sinh nên gần như không xảy ra — không được nuốt im lặng
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Warning,
                    Category = LogCategory.Request,
                    Message = $"Không enqueue được request {id}.",
                    RequestId = id,
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                    "server_error", null, null);
                return;
            }

            // Đăng ký SAU Enqueue: dispatcher đã Take thì TryRemove false → serve tự cắt stream (spec §3.4)
            ctx.RequestAborted.Register(() =>
            {
                if (queue.TryRemove(id, out var removed))
                {
                    log.Write(new LogEntry
                    {
                        Severity = LogSeverity.Info,
                        Category = LogCategory.Request,
                        Message = $"Request {id} bị client ngắt khi đang chờ.",
                        RequestId = id,
                        ClientKeyId = ClientKeyItems.IdOf(ctx),
                    });
                    removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
                }
            });

            var outcome = await request.Completion.Task;

            if (outcome is DispatchOutcome.Error error)
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, error.Status, error.Message,
                    error.Type, error.Param, error.Code);
            }
            else if (outcome is DispatchOutcome.Passthrough passthrough)
            {
                // Ghi nguyên response cuối — byte passthrough không JSON wrap (spec 3C §3.3);
                // Retry-After copy lại cho client (§3.6)
                ctx.Response.StatusCode = passthrough.Status;
                if (passthrough.ContentType is not null)
                    ctx.Response.ContentType = passthrough.ContentType;
                if (passthrough.RetryAfterHeader is not null)
                    ctx.Response.Headers["Retry-After"] = passthrough.RetryAfterHeader;
                await ctx.Response.Body.WriteAsync(passthrough.Body, ctx.RequestAborted);
            }
            else if (outcome is DispatchOutcome.Retryable)
            {
                // Dispatcher đã convert Retryable → Passthrough/Error (spec §2.2) — tới đây là bug
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Error,
                    Category = LogCategory.Request,
                    Message = $"Outcome Retryable lọt tới endpoint request {id}.",
                    RequestId = id,
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                    "server_error", null, null);
            }
            else if (outcome is DispatchOutcome.Cancelled
                     && !ctx.RequestAborted.IsCancellationRequested)
            {
                // Huỷ qua control API (client còn kết nối) — ghi log + 400 request_cancelled (spec §5)
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Info,
                    Category = LogCategory.Request,
                    Message = $"Đã huỷ request {id} (đang chờ), model {prepared.ModelId}.",
                    RequestId = id,
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
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

            // Quyết định #10 (design 2026-09-25): client chọn combo qua trường `model` —
            // combo phải có trong list để client (opencode...) phát hiện trước khi gọi chat.
            // Combo không có cờ Enabled (khác Model) nên trả toàn bộ, xếp sau model trực tiếp.
            var combos = await db.Combos.AsNoTracking()
                .OrderBy(c => c.Id)
                .Select(c => new
                {
                    id = c.Name,
                    @object = "model",
                    created = c.CreatedAt.ToUnixTimeSeconds(),
                    owned_by = "combo",
                })
                .ToListAsync(http.RequestAborted);

            return Results.Json(new { @object = "list", data = models.Concat(combos).ToList() });
        });

        // Snapshot request đang chờ + đang phục vụ (spec §3.5) — đọc 2 nguồn trong 1 lần,
        // KHÔNG đồng bộ hóa: id có thể chuyển state giữa 2 lần đọc, cancel tự chịu 404/409
        app.MapGet("/v1/requests", (IRequestQueue queue, IExecutionList executions) =>
        {
            var now = DateTimeOffset.UtcNow;
            var queued = queue.Snapshot().Select(r => new
            {
                id = r.Id,
                state = "queued",
                priority = SnapshotPriority(r.Priority),
                model = r.Model,
                provider = (string?)null,
                account = (string?)null,
                enqueuedAt = r.EnqueuedAt,
                startedAt = (DateTimeOffset?)null,
                elapsedMs = (long)(now - r.EnqueuedAt).TotalMilliseconds,
                cancelable = true,
            });
            var serving = executions.Snapshot().Select(e => new
            {
                id = e.RequestId,
                state = "serving",
                priority = SnapshotPriority(e.Priority),
                model = e.Model,
                provider = (string?)e.ProviderName,
                account = string.IsNullOrEmpty(e.AccountName) ? null : e.AccountName,
                enqueuedAt = e.EnqueuedAt,
                startedAt = (DateTimeOffset?)e.StartedAt,
                elapsedMs = (long)(now - e.StartedAt).TotalMilliseconds,
                cancelable = false,
            });

            return Results.Json(new { requests = queued.Concat(serving).ToList() });
        });

        // Chỉ huỷ được request còn trong queue (spec §3.4): 200 / 409 / 404
        app.MapPost("/v1/requests/{id}/cancel", async (string id, HttpContext ctx,
            IRequestQueue queue, IExecutionList executions) =>
        {
            // TryRemove atomic với Take — thắng thì 200, thua thì rơi vào nhánh 409/404
            if (queue.TryRemove(id, out var removed))
            {
                removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
                await Results.Json(new { cancelled = true }).ExecuteAsync(ctx);
                return;
            }

            if (executions.Contains(id))
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 409,
                    $"The request '{id}' is not cancellable", "invalid_request_error", null,
                    "not_cancellable");
                return;
            }

            await ChatCompletionsHandler.WriteErrorAsync(ctx, 404,
                $"The request '{id}' does not exist", "invalid_request_error", null,
                "request_not_found");
        });
    }

    /// <summary>
    /// Snapshot map ngược vocab input: Highest → "max" (không phải "highest") để client
    /// gửi thẳng giá trị này lại làm <c>X-Priority</c> — parser chỉ nhận high/max (chốt kỹ thuật §10).
    /// </summary>
    private static string SnapshotPriority(RequestPriority priority) => priority switch
    {
        RequestPriority.Highest => "max",
        RequestPriority.High => "high",
        _ => "normal",
    };
}
