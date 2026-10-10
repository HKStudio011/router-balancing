using System.IO.Pipelines;
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

        // Cancel: nguồn sự thật DUY NHẤT cho HTTP endpoint và nút Huỷ trong UI (spec §5.1)
        builder.Services.AddSingleton<IRequestCancelService, RequestCancelService>();

        // Đổi ưu tiên: nguồn sự thật cho nút đổi priority trong RequestDetailModal —
        // cùng IRequestQueue.SetPriority với luật 1-Highest và publish trace G3
        builder.Services.AddSingleton<IRequestPriorityService, RequestPriorityService>();

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
        builder.Services.AddSingleton<ProxyRequestHandler>();
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
            (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ProxyRequestHandler handler, ILogService log, ITraceFeed trace,
                IApiMonitorStore monitor) =>
                RunQueueFirstAsync(ctx, queue, executions, handler, log, trace, monitor,
                    ProxyProtocols.Chat, ProxyEndpoint.Chat));

        // /v1/responses dùng chung runner — khác đúng protocol + endpoint flag (spec v1-responses §3.1)
        app.MapPost("/v1/responses",
            (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ProxyRequestHandler handler, ILogService log, ITraceFeed trace,
                IApiMonitorStore monitor) =>
                RunQueueFirstAsync(ctx, queue, executions, handler, log, trace, monitor,
                    ProxyProtocols.Responses, ProxyEndpoint.Responses));

        MapEndpoints(app);
    }

    /// <summary>
    /// Runner queue-first dùng chung cho mọi endpoint (spec v1-responses §3.1):
    /// id → prepare (validate qua <paramref name="protocol"/>) → priority → pipe → enqueue →
    /// H1+Received → abort-register → stream loop → outcome → H4 → ghi response.
    /// Body là pure move của lambda chat Task 3B — hành vi chat không đổi, chat integration
    /// tests là gate hồi quy.
    /// </summary>
    /// <param name="protocol">Protocol của endpoint — quyết định validate/path/tee.</param>
    /// <param name="endpoint">Đích lưu trong <see cref="ProxyRequest"/> — dispatcher resolve protocol theo giá trị này.</param>
    internal static async Task RunQueueFirstAsync(HttpContext ctx, IRequestQueue queue,
        IExecutionList executions, ProxyRequestHandler handler, ILogService log, ITraceFeed trace,
        IApiMonitorStore monitor, IProxyProtocol protocol, ProxyEndpoint endpoint)
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

        var prepared = await handler.PrepareAsync(ctx, protocol);
        if (prepared is null)
            return; // validate fail — PrepareAsync đã ghi 400, chưa enqueue

        var priority = RequestPriorityParser.Parse(ctx.Request.Headers["X-Priority"].ToString());
        var request = new ProxyRequest(id, priority, prepared.ModelId, prepared.Body, ctx,
            endpoint);

        // Pipe PHẢI vào Items TRƯỚC Enqueue (spec §2.3, two-writer): slot trống thì
        // dispatcher có thể serve ngay — nếu key chưa publish, handler 2xx write thẳng
        // Response.Body trong khi endpoint viết keep-alive = framing corruption, cộng
        // Dictionary access không synchronized (happens-before qua lock của queue).
        // Header commit vẫn giữ SAU enqueue (spec §4.1 — enqueue fail không được gửi 200).
        Pipe? ssePipe = null;
        if (prepared.IsStream)
        {
            ssePipe = new Pipe();
            ctx.Items[SsePipeItems.Key] = ssePipe;
        }

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
            await ProxyRequestHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                "server_error", null, null);
            return;
        }

        // H1 monitor: record vào ring tại đây (store fail-open — không try/catch ở call site);
        // đồng bộ với trace — request không qua enqueue (validate 400) không hiện trong monitor
        monitor.StartRequest(id, request.Model, prepared.Body);
        trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model,
            null, null, null, null, null, DateTimeOffset.Now,
            Priority: request.Priority,
            Endpoint: endpoint.ToString().ToLowerInvariant()));

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

        // Stream (spec early-headers §3.2): commit 200+event-stream NGAY sau enqueue —
        // client nhận head khi request còn trong queue; content/keep-alive/in-band error
        // do loop phía dưới forward qua pipe. Non-stream giữ nguyên đường cũ.
        DispatchOutcome outcome;
        long contentBytes = 0;   // CHỈ byte từ pipe — keep-alive không đếm (phân loại #7/#8)
        if (prepared.IsStream)
        {
            var pipe = ssePipe!; // đã publish vào Items TRƯỚC enqueue — xem comment trên
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            try
            {
                await ctx.Response.StartAsync(ctx.RequestAborted);
                // StartAsync chỉ chạy OnStarting; head (Kestrel lẫn TestServer) chỉ thực sự
                // đi ở lần flush/complete đầu tiên — flush 0 byte tường minh để head tới
                // client khi request còn queued (đây là chính feature, không phải test-patch).
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                // G2: head đã commit 200 — đánh dấu cho trace (stream only; non-stream không có flush point)
                trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model,
                    null, null, null, null, null, DateTimeOffset.Now, HeadersSent: true));
                (outcome, contentBytes) =
                    await RunStreamLoopAsync(ctx, pipe, request.Completion.Task);
            }
            catch (OperationCanceledException)
            {
                // Client ngắt trước khi header commit — Register/Dispatcher đã set outcome;
                // bỏ pipe khỏi Items để nhánh ghi đi đường non-stream như cũ
                ctx.Items.Remove(SsePipeItems.Key);
                ssePipe = null;
                outcome = await request.Completion.Task;
            }
        }
        else
        {
            outcome = await request.Completion.Task;
        }

        // Publish tại đây (TRƯỚC khối ghi response): viết response có thể nổ
        // (client ngắt giữa passthrough) — nếu publish sau thì circle terminal bị mồ côi.
        var (stage, success, status) = ClassifyOutcome(outcome);
        // Handled: status upstream thật đã ghi vào ctx.Response trước khi stream — H4 phải
        // mang status cuối (spec api-monitor §3.1: Status = HTTP status trả về; monitor
        // cần 200 cho record Done). ClassifyOutcome không nắm ctx nên trả null cho Handled.
        if (outcome is DispatchOutcome.Handled)
            status = ctx.Response.StatusCode;
        // Override đỏ #7 (spec early-headers §4.3): stream chết TRƯỚC khi có byte content
        // nào (Aborted, 0 contentBytes, client còn nối) = lỗi server, không phải client
        // ngắt — row đỏ status 500 dù wire là 200.
        if (prepared.IsStream && contentBytes == 0 && outcome is DispatchOutcome.Aborted
            && !ctx.RequestAborted.IsCancellationRequested)
        {
            stage = TraceStage.Finished;
            success = false;
            status = 500;
        }
        trace.Publish(new TraceEvent(id, stage, prepared.ModelId,
            null, null, status, success, null, DateTimeOffset.Now));

        if (ssePipe is not null)
        {
            // Head đã commit 200 — lỗi sau đó là event in-band, không phải status HTTP
            // (spec early-headers §3.5). OCE khi write (client vừa chết) được phép
            // propagate: publish H4 đã chạy TRƯỚC write, giống hành vi passthrough cũ.
            switch (outcome)
            {
                case DispatchOutcome.Error inBandError:
                    await WriteInBand(ctx, SseErrorEvent.Error(inBandError.Status,
                        inBandError.Message, inBandError.Type, inBandError.Param,
                        inBandError.Code));
                    break;
                case DispatchOutcome.Passthrough inBandPassthrough:
                    await WriteInBand(ctx, SseErrorEvent.Passthrough(inBandPassthrough.Status,
                        inBandPassthrough.Body, inBandPassthrough.RetryAfterHeader));
                    break;
                case DispatchOutcome.Retryable or DispatchOutcome.Fatal:
                    // Dispatcher đã convert Retryable/Fatal → Passthrough/Error (spec §2.2) — tới đây là bug
                    log.Write(new LogEntry
                    {
                        Severity = LogSeverity.Error,
                        Category = LogCategory.Request,
                        Message = $"Outcome nội bộ (Retryable/Fatal) lọt tới endpoint request {id}.",
                        RequestId = id,
                        ClientKeyId = ClientKeyItems.IdOf(ctx),
                    });
                    await WriteInBand(ctx, SseErrorEvent.ServerFault());
                    break;
                case DispatchOutcome.Cancelled when !ctx.RequestAborted.IsCancellationRequested:
                    // Huỷ qua control API (client còn kết nối) — ghi log + event request_cancelled (spec §5)
                    log.Write(new LogEntry
                    {
                        Severity = LogSeverity.Info,
                        Category = LogCategory.Request,
                        Message = $"Đã huỷ request {id} (đang chờ), model {prepared.ModelId}.",
                        RequestId = id,
                        ClientKeyId = ClientKeyItems.IdOf(ctx),
                    });
                    await WriteInBand(ctx, SseErrorEvent.Cancelled());
                    break;
                case DispatchOutcome.Aborted when contentBytes == 0
                    && !ctx.RequestAborted.IsCancellationRequested:
                    // Upstream chết trước tee, chưa byte nào tới client — fault in-band (#7)
                    await WriteInBand(ctx, SseErrorEvent.ServerFault());
                    break;
                // Handled / Cancelled+client-abort / Aborted mid-content: không ghi gì —
                // content đã forward, hoặc kết nối đã đóng (row vàng Canceled qua H4)
            }
        }
        else if (outcome is DispatchOutcome.Error error)
        {
            await ProxyRequestHandler.WriteErrorAsync(ctx, error.Status, error.Message,
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
            await ProxyRequestHandler.WriteErrorAsync(ctx, 500, "Internal server error",
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
            await ProxyRequestHandler.WriteErrorAsync(ctx, 400, "Request cancelled.",
                "invalid_request_error", null, "request_cancelled");
        }
        // Handled / Aborted / Cancelled do client ngắt: response đã ghi hoặc kết nối đã đóng

        static Task WriteInBand(HttpContext ctx, byte[] evt) =>
            ctx.Response.Body.WriteAsync(evt, ctx.RequestAborted).AsTask();
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
            IRequestCancelService cancelSvc) =>
        {
            // TryRemove atomic với Take — logic nằm trong IRequestCancelService (spec §5.1);
            // thắng thì 200, thua thì rơi vào nhánh 409/404
            switch (cancelSvc.Cancel(id))
            {
                case RequestCancelResult.Cancelled:
                    await Results.Json(new { cancelled = true }).ExecuteAsync(ctx);
                    break;
                case RequestCancelResult.NotCancellable:
                    await ProxyRequestHandler.WriteErrorAsync(ctx, 409,
                        $"The request '{id}' is not cancellable", "invalid_request_error", null,
                        "not_cancellable");
                    break;
                // NotRunning không tới được từ service (nó không biết trạng thái host) —
                // gộp NotFound để switch exhaustive, tránh rơi ra ngoài khi enum thêm giá trị
                case RequestCancelResult.NotFound:
                case RequestCancelResult.NotRunning:
                    await ProxyRequestHandler.WriteErrorAsync(ctx, 404,
                        $"The request '{id}' does not exist", "invalid_request_error", null,
                        "request_not_found");
                    break;
            }
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
    /// Chu kỳ keep-alive SSE — LLM first-token có thể hàng chục giây; dòng comment giữ
    /// kết nối sống qua proxy/client timeout (spec early-headers §3.4).
    /// </summary>
    private const int KeepAliveIntervalMs = 5000;

    /// <summary>Frame SSE comment — parser bỏ qua, chỉ có tác dụng đẩy byte trên wire.</summary>
    private static readonly byte[] KeepAliveComment = ": keep-alive\n\n"u8.ToArray();

    /// <summary>
    /// Forward pipe → Response.Body trong lúc chờ outcome: quá hạn chưa có data thì viết
    /// keep-alive; outcome về thì complete writer và drain nốt phần còn lại. Trả số byte
    /// content đã forward (keep-alive KHÔNG đếm) — quyết định phân loại #7/#8 ở caller.
    /// </summary>
    /// <param name="ctx">Context đã commit head 200+event-stream (flush point ở caller).</param>
    /// <param name="pipe">Pipe handler 2xx dùng làm dest tee.</param>
    /// <param name="completion">Task outcome của request — endpoint là người chờ duy nhất.</param>
    /// <returns>Outcome cuối và tổng byte content đã ghi xuống wire.</returns>
    private static async Task<(DispatchOutcome Outcome, long ContentBytes)> RunStreamLoopAsync(
        HttpContext ctx, Pipe pipe, Task<DispatchOutcome> completion)
    {
        var ct = ctx.RequestAborted;
        var readTask = pipe.Reader.ReadAsync(ct).AsTask();
        long contentBytes = 0;
        try
        {
            while (true)
            {
                var winner = await Task.WhenAny(readTask,
                    Task.Delay(KeepAliveIntervalMs, ct), completion);
                if (winner == completion)
                    break;
                if (winner == readTask)
                {
                    var result = await readTask;
                    if (result.Buffer.Length > 0)
                    {
                        foreach (var segment in result.Buffer)
                        {
                            await ctx.Response.Body.WriteAsync(segment, ct);
                            contentBytes += segment.Length;
                        }
                        pipe.Reader.AdvanceTo(result.Buffer.End);
                    }
                    else
                    {
                        // Read rỗng (writer chưa có gì) — chỉ re-arm, không advance
                        pipe.Reader.AdvanceTo(result.Buffer.Start);
                    }
                    readTask = pipe.Reader.ReadAsync(ct).AsTask();
                }
                else
                {
                    // KeepAliveIntervalMs trôi qua chưa có data → comment line giữ kết nối
                    await ctx.Response.Body.WriteAsync(KeepAliveComment, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client ngắt giữa loop: Register/Dispatcher LUÔN set outcome khi abort —
            // await completion không deadlock (đó là lý do abort không cần timeout ở đây)
            return (await completion, contentBytes);
        }

        // Completion về: endpoint là completer duy nhất của pipe (handler không Complete).
        // readTask đang pending sẽ về cùng buffer cuối khi writer complete — PHẢI await lại
        // task đó (không ReadAsync mới: một Reader không cho hai ReadAsync đồng thời).
        pipe.Writer.Complete();
        // Drain PHẢI ghi nốt buffer còn lại và cộng contentBytes: Aborted mid-content tới đây
        // khi pipe còn byte chưa forward (dispatcher set Aborted sau khi handler ném) —
        // bỏ qua drain thì contentBytes==0 → nhầm in-band #7 thay vì im lặng #8.
        while (true)
        {
            try
            {
                var result = await readTask;
                if (result.Buffer.Length > 0)
                {
                    foreach (var segment in result.Buffer)
                    {
                        await ctx.Response.Body.WriteAsync(segment, CancellationToken.None);
                        contentBytes += segment.Length;
                    }
                }
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
                readTask = pipe.Reader.ReadAsync(CancellationToken.None).AsTask();
            }
            catch (Exception)
            {
                // IOException/OCE khi client đã chết lúc drain — KHÔNG propagate: publish H4
                // chưa chạy (nằm sau outcome ở caller) → phải rơi về classification như chủ
                // đích "viết response có thể nổ" đã phòng. contentBytes tới đây đủ phân loại.
                break;
            }
        }
        return (await completion, contentBytes);
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
