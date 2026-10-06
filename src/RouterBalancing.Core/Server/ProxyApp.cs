using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
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

        if (!builder.Services.Any(d => d.ServiceType == typeof(ITraceFeed)))
            builder.Services.AddSingleton<ITraceFeed, TraceFeed>(); // fallback cho test harness; app đã đăng ký instance ở StartAsync

        if (!builder.Services.Any(d => d.ServiceType == typeof(IApiMonitorStore)))
            builder.Services.AddSingleton<IApiMonitorStore, ApiMonitorStore>(); // fallback test harness; app đã đăng ký instance ở StartAsync

        builder.Services.AddTransient<ProxyHealthHandler>();
        // Resolver singleton: dispatch theo provider+account (most-specific-wins, D1)
        builder.Services.AddSingleton<IProxySelectionResolver, ProxySelectionResolver>();

        // Chính sách timeout đường forward chat (2026-10-04): app KHÔNG tự cắt request —
        // client→app và app→provider không có timeout; client→provider do client quyết
        // định qua disconnect (RequestAborted). Timeout 100s (default) cắt SSE giữa chừng,
        // ConnectTimeout finite cắt kết nối đang mở tới provider — cả hai đặt vô hạn.
        // Proxy + health handler: outbound request qua pool (spec proxy-pool §4),
        // hết proxy sống thì handler tự attempt direct cuối.
        builder.Services.AddHttpClient(OpenAiUpstreamClient.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateUpstreamHandler)
            .AddHttpMessageHandler<ProxyHealthHandler>();

        // AddHttpMessageHandler<THandler> chỉ resolve THandler từ DI, không tự đăng ký
        // ("must be registered as a transient service") — thiếu dòng này thì lần gửi
        // đầu qua client provider-probe ném InvalidOperationException lúc build pipeline
        builder.Services.AddTransient<ProviderProbeTimeoutHandler>();
        // provider-probe (spec manual-retry §3.5, V6): client probe provider
        // trong container proxy — connect 60s (G6), per-request timeout qua
        // ProviderProbeTimeoutHandler (đổi setting có hiệu lực ngay), proxy pool y hệt MauiProgram
        builder.Services.AddHttpClient(ProviderRequestFactory.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                Proxy = new RoundRobinWebProxy(),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(60),
            })
            .AddHttpMessageHandler<ProxyHealthHandler>()
            .AddHttpMessageHandler<ProviderProbeTimeoutHandler>();

        // Queue-first 3B (spec §2.1): endpoint chỉ enqueue + chờ outcome;
        // DispatcherLoop (hosted service) resolve → chọn → serve.
        builder.Services.AddSingleton<IRequestQueue, RequestQueue>();
        builder.Services.AddSingleton<IExecutionList, ExecutionList>();
        builder.Services.AddSingleton<IComboResolver, ComboResolver>();
        builder.Services.AddSingleton<IModelSelector, ModelSelector>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
        // Đồng hồ system — service time-sensitive trong proxy container
        // (ClientKeyRateLimiter) resolve cùng 1 instance
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IClientKeyRateLimiter, ClientKeyRateLimiter>();
        // Singleton (không factory): cache phải sống 1 lần/proxy container để event KeysChanged
        // attach đúng 1 lần; Dispose của container unsubscribe khi proxy dừng.
        builder.Services.AddSingleton<ClientKeyAuthCache>();
        builder.Services.AddSingleton<IClientKeyUsageSink, ClientKeyUsageSink>();
        builder.Services.AddHostedService<DispatcherLoop>();
    }

    /// <summary>
    /// Primary handler cho client "upstream" (forward chat) — <see cref="SocketsHttpHandler.ConnectTimeout"/>
    /// đặt vô hạn theo chính sách timeout (2026-10-04): app → provider không có timeout,
    /// app không được tự cắt request; client ngắt thì <c>RequestAborted</c> cắt.
    /// Tách thành factory để test lock policy này.
    /// </summary>
    internal static SocketsHttpHandler CreateUpstreamHandler() => new()
    {
        Proxy = new RoundRobinWebProxy(),
        UseProxy = true,
        ConnectTimeout = Timeout.InfiniteTimeSpan,
    };

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
                ChatCompletionsHandler handler, ILogService log, ITraceFeed trace,
                IApiMonitorStore monitor) =>
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

            // H1 monitor: record vào ring tại đây (store fail-open — không try/catch ở call site);
            // đồng bộ với trace — request không qua enqueue (validate 400) không hiện trong monitor
            monitor.StartRequest(id, request.Model, prepared.Body);
            trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model,
                null, null, null, null, null, DateTimeOffset.Now));

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

            // Publish tại đây (TRƯỚC khối ghi response): viết response có thể nổ
            // (client ngắt giữa passthrough) — nếu publish sau thì circle terminal bị mồ côi.
            var (stage, success, status) = ClassifyOutcome(outcome);
            // Handled: status upstream thật đã ghi vào ctx.Response trước khi stream — H4 phải
            // mang status cuối (spec api-monitor §3.1: Status = HTTP status trả về; monitor
            // cần 200 cho record Done). ClassifyOutcome không nắm ctx nên trả null cho Handled.
            if (outcome is DispatchOutcome.Handled)
                status = ctx.Response.StatusCode;
            trace.Publish(new TraceEvent(id, stage, prepared.ModelId,
                null, null, status, success, null, DateTimeOffset.Now));

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
            else if (outcome is DispatchOutcome.Retryable or DispatchOutcome.Fatal)
            {
                // Dispatcher đã convert Retryable/Fatal → Passthrough/Error (spec §2.2) — tới đây là bug
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Error,
                    Category = LogCategory.Request,
                    Message = $"Outcome nội bộ (Retryable/Fatal) lọt tới endpoint request {id}.",
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
        // /health mở luôn (middleware bỏ qua path này) — monitor/ping bên ngoài dùng
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
    /// Chuyển outcome của endpoint sang (stage, success, status) cho trace event H4.
    /// </summary>
    internal static (TraceStage Stage, bool? Success, int? Status) ClassifyOutcome(DispatchOutcome outcome) =>
        outcome switch
        {
            DispatchOutcome.Handled => (TraceStage.Finished, true, null),
            DispatchOutcome.Passthrough p => (TraceStage.Finished, p.Status < 400, p.Status),
            DispatchOutcome.Error e => (TraceStage.Finished, false, e.Status),
            DispatchOutcome.Cancelled => (TraceStage.Canceled, null, null),
            DispatchOutcome.Aborted => (TraceStage.Canceled, null, null),
            // Retryable/Fatal là tín hiệu nội bộ — dispatcher đã convert trước khi outcome
            // về endpoint, nên 2 nhánh này chỉ là bug-path defensive (không được nuốt im lặng
            // nếu invariant vỡ): map theo status (null = lỗi mạng) để trace vẫn ghi được.
            DispatchOutcome.Retryable r => (TraceStage.Finished, false, r.Status),
            DispatchOutcome.Fatal f => (TraceStage.Finished, false, f.Status),
            // DispatchOutcome public abstract — subclass thêm sau này (ngoài assembly) không
            // cần sửa chỗ này: map an toàn là finished + failed + không status, không nuốt event
            _ => (TraceStage.Finished, false, null),
        };

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
