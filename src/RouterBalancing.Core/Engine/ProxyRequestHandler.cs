using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phần handler của pipeline queue-first (3B): <see cref="PrepareAsync"/> (buffer + validate —
/// endpoint gọi trước enqueue) và <see cref="ForwardAsync"/> (key → upstream → stream —
/// dispatcher gọi khi đã giữ slot). Protocol-agnostic: 3 điểm protocol-specific (validate,
/// path + rewrite body, tee) đi qua <see cref="IProxyProtocol"/> (spec v1-responses §3.2).
/// Singleton, không giữ state per-request.
/// </summary>
public sealed class ProxyRequestHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log,
    IRequestUsageSink usageSink,
    IApiMonitorStore monitor,
    IAppSettingsService settings,
    IDatasetSink dataset,
    IProxySelectionResolver proxySelectionResolver,
    IProxyPool proxyPool)
{
    /// <summary>Body đã buffer + model id đã validate — input cho enqueue.</summary>
    /// <param name="ModelId">Model id đã validate (resolve alias tại dispatcher).</param>
    /// <param name="Body">Body JSON gốc từng byte — forward nguyên vẹn.</param>
    /// <param name="IsStream">Body có <c>"stream": true</c> — cho phép flush header sớm (spec early-headers).</param>
    public sealed record PreparedProxyRequest(string ModelId, byte[] Body, bool IsStream);

    /// <summary>
    /// Buffer body rồi validate qua <paramref name="protocol"/> (rule 3A cho chat).
    /// Lỗi validate → ghi 400 OpenAI-style NGAY từ message/param của protocol
    /// (không qua queue, header <c>X-Request-Id</c> đã được endpoint set trước đó) và trả
    /// <see langword="null"/>; hợp lệ → trả prepared request.
    /// </summary>
    /// <param name="ctx">HttpContext của request gốc.</param>
    /// <param name="protocol">Protocol của endpoint đang xử lý.</param>
    public async Task<PreparedProxyRequest?> PrepareAsync(HttpContext ctx, IProxyProtocol protocol)
    {
        var ct = ctx.RequestAborted;

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var validation = protocol.Validate(body);
        if (!validation.IsValid)
        {
            // Spec §7: row validate-fail vẫn phải correlate được request — middleware đã set
            // Items vào ctx nên gắn RequestId/ClientKeyId như các row Write khác; Message dùng
            // đúng ErrorMessage của protocol (switch map failure→text đã chuyển vào protocol).
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategory.Request,
                Message = $"Yêu cầu không hợp lệ: {validation.ErrorMessage}",
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
            await WriteErrorAsync(ctx, 400, validation.ErrorMessage!, "invalid_request_error",
                validation.ErrorParam, null);
            return null;
        }

        return new PreparedProxyRequest(validation.ModelId!, body, validation.IsStream);
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
    /// <param name="protocol">Protocol của endpoint — quyết định path/rewrite/tee (spec v1-responses §3.2).</param>
    /// <param name="ct">Token — dùng <c>ctx.RequestAborted</c> để disconnect cắt stream.</param>
    public async Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
        byte[] body, long accountId, IProxyProtocol protocol, CancellationToken ct)
    {
        // `!`: endpoint set ctx.Items[RequestId] TRƯỚC enqueue (ProxyApp) — ForwardAsync chỉ
        // chạy cho request đã qua endpoint nên id luôn có; hook monitor cần string không-null
        var requestId = ClientKeyItems.RequestIdOf(ctx)!;
        // Gate dataset export đọc MỘT LẦN ở đầu (spec §7.2): truyền xuống tee qua captureCompletion;
        // tắt → tee y hệt hôm nay, không tốn gì trên đường forward.
        var datasetOn = settings.DatasetExportEnabled;
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

        // Đặt context proxy TRƯỚC khi gọi handler (upstream.PostAsync) —
        // ProxyHealthHandler đọc ProxyTarget.Current để dispatch theo assignment.
        ProxyTarget.Current.Value = new ProxyTarget(provider, account);
        // Linked tới RequestAborted: khi handler cắt (upstream chết giữa chừng) Cancel() đóng kết
        // nối HTTP tới upstream NGAY — chuẩn OpenAI SDK: không có cancel endpoint cho streaming
        // chat (openai-openapi đã xác minh) nên "cancel" = abort kết nối. Hủy TRƯỚC khi
        // outcome/exception rời handler, trước khi endpoint ghi lỗi cho client (cancel-before-error).
        using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Rewrite protocol-specific (alias→model id thật, inject usage flags cho chat) —
            // body gốc giữ nguyên ở queue/prepare
            var (requestBody, expectsUsage) = protocol.PrepareUpstreamBody(body, model.ModelId,
                provider.Type);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await upstream.PostAsync(provider, key, protocol.UpstreamPath, requestBody,
                    requestId, upstreamCts.Token);
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
                // status 0 = không có HTTP status → store ghi FailureKind "network";
                // State vẫn do feed H4 chốt (record có thể retry tiếp ở attempt/advance kế)
                monitor.RecordError(requestId, 0, null);
                return new DispatchOutcome.Fatal(FailoverLevel.Provider, null, null, [], null);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    // 2xx giữ nguyên 3A/3B: stream thẳng — tee quét usage trong lúc copy (spec §6.1)
                    // Sau flush point (stream) head đã gửi — set status/content-type vô nghĩa,
                    // chỉ ghi khi chưa commit; non-stream không bao giờ có pipe → luôn vào đây (như cũ).
                    var upstreamSse = string.Equals(
                        response.Content.Headers.ContentType?.MediaType, "text/event-stream",
                        StringComparison.OrdinalIgnoreCase);
                    var hasPipe = ctx.Items.TryGetValue(SsePipeItems.Key, out var pipeObj);
                    Stream dest = hasPipe
                        ? ((Pipe)pipeObj!).Writer.AsStream()
                        : ctx.Response.Body;
                    // Client non-stream nhưng upstream trả SSE (ChatBody ép stream:true; sidecar
                    // OpenCode cũng ép vì gate 403 stream:false) → aggregate về JSON cho client,
                    // giữ đúng contract non-stream
                    var aggregate = upstreamSse && !hasPipe;
                    if (!ctx.Response.HasStarted)
                    {
                        ctx.Response.StatusCode = (int)response.StatusCode;
                        if (aggregate)
                            ctx.Response.ContentType = "application/json";
                        else if (response.Content.Headers.ContentType is { } okType)
                            ctx.Response.ContentType = okType.ToString();
                        else
                            // Upstream 2xx KHÔNG gửi Content-Type (hay gặp ở gateway self-host / ollama):
                            // copy nguyên làm response cũng thiếu media type → SDK OpenAI không parse.
                            // Endpoint này là OpenAI-compatible nên JSON là hợp đồng mặc định.
                            ctx.Response.ContentType = "application/json";
                    }
                    // Rethrow: KHÔNG đổi hành vi xử lý lỗi cho pipeline — dispatcher vẫn map
                    // HasStarted → Aborted như cũ (row vàng khớp circle, spec api-monitor §7).
                    // Nhưng RecordResponse chạy SAU tee nên mid-stream fail mất TTFT/ResponseBody —
                    // ghi RecordError(0) để popup có FailureKind=network thay vì trống.
                    UsageCapture.TeeResult tee;
                    // Watchdog idle-read (setting upstreamIdleTimeoutSec — 0 = tắt; đọc mỗi lần
                    // bắt đầu stream): bọc content tại seam duy nhất để MỌI điểm đọc (usage tee,
                    // aggregate, protocol) đi qua IdleWatchdogStream — chống treo vô hạn khi
                    // HttpClient.Timeout = infinite (upstream im lặng không RST → đen màn hình).
                    var idleSec = settings.UpstreamIdleTimeoutSec;
                    var teeContent = idleSec > 0
                        ? new UpstreamWatchdogContent(response.Content, TimeSpan.FromSeconds(idleSec))
                        : response.Content;
                    try
                    {
                        // Stream sau flush: dest là pipe endpoint đặt tại flush point — endpoint
                        // là tay duy nhất ghi Response.Body và completer duy nhất của pipe, nên
                        // handler KHÔNG dispose/Complete writer ở đây (spec early-headers §3.2).
                        // captureCompletion chỉ cho đường tee: aggregate không nhận tham số (spec §7.3 —
                        // completion của nó đã nằm trong ResponseBody, lấy ở khối capture bên dưới)
                        tee = aggregate
                            ? await protocol.AggregateSseAsync(teeContent, dest, upstreamCts.Token)
                            : await protocol.TeeAsync(teeContent, dest, upstreamCts.Token,
                                captureCompletion: datasetOn);
                    }
                    catch (UpstreamDiedBeforeContentException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        // Hủy kết nối upstream NGAY (cancel-before-error): không thì response sống
                        // tới khi dispose, upstream vẫn sinh token sau khi attempt đã đi tiếp
                        upstreamCts.Cancel();
                        // Upstream cắt TRƯỚC byte content nào — retry an toàn vì client chưa nhận gì;
                        // trả Fatal(Provider, Status null) → dispatcher retry transient rồi failover,
                        // cùng contract với lỗi kết nối lúc gửi (spec exhaustive-failover §3.1).
                        log.Write(new LogEntry
                        {
                            Severity = LogSeverity.Error,
                            Category = LogCategory.Request,
                            Message = $"Upstream '{provider.Name}' cắt kết nối trước khi gửi content — sẽ retry/failover.",
                            Details = (ex.InnerException ?? ex).ToString(),
                            ErrorCode = (ex.InnerException ?? ex).GetType().Name,
                            RequestId = ClientKeyItems.RequestIdOf(ctx),
                            ClientKeyId = ClientKeyItems.IdOf(ctx),
                        });
                        // status 0 → store ghi FailureKind "network" (popup hiển thị đúng nhóm lỗi)
                        monitor.RecordError(requestId, 0, null);
                        return new DispatchOutcome.Fatal(FailoverLevel.Provider, null, null, [], null);
                    }
                    catch (Exception ex)
                    {
                        // Hủy kết nối upstream TRƯỚC khi exception rời handler — endpoint và
                        // dispatcher mới ghi lỗi cho client sau đó (cancel-before-error, chuẩn
                        // OpenAI SDK: cancel = abort kết nối HTTP — không có cancel endpoint
                        // cho streaming chat); idempotent khi token đã hủy vì client ngắt
                        upstreamCts.Cancel();
                        // Client cancel mid-stream không phải lỗi mạng — chỉ ghi RecordError
                        // khi client còn nối (upstream chết giữa chừng). Bỏ guard này thì row
                        // Canceled bị gắn FailureKind=network gây nhầm lẫn khi chẩn đoán.
                        if (!ctx.RequestAborted.IsCancellationRequested)
                            monitor.RecordError(requestId, 0, null);
                        // Điểm #1 (spec §4.1): nơi DUY NHẤT còn stopwatch/account/route — exception rời handler
                        // thì dispatcher chỉ còn message. Warning (không Error) đúng D1; chỉ ghi khi client còn
                        // nối vì client tự ngắt không phải sự cố upstream (D4).
                        if (!ctx.RequestAborted.IsCancellationRequested)
                        {
                            try
                            {
                                log.Write(new LogEntry
                                {
                                    Severity = LogSeverity.Warning,
                                    Category = LogCategory.Request,
                                    Message = $"Upstream '{provider.Name}' đứt stream sau {stopwatch.ElapsedMilliseconds}ms "
                                              + $"— account '{account.Name}', route {DescribeProxyRoute(provider, account)}.",
                                    Details = ex.ToString(),
                                    // Type lỗi đọc GỐC (watchdog bọc thành UpstreamStreamDiedException) — cùng quy ước
                                    // với catch pre-content để filter được HttpIOException trong DB
                                    ErrorCode = (ex.InnerException ?? ex).GetType().Name,
                                    RequestId = ClientKeyItems.RequestIdOf(ctx),
                                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                                });
                            }
                            catch
                            {
                                // Nuốt chủ đích: chẩn đoán không được thay thế exception gốc đang rethrow
                            }
                        }
                        throw;
                    }
                    // Monitor ghi NGAY sau tee, KỂ CẢ khi upstream thiếu usage (token null) —
                    // store fail-open nên không try/catch ở call site (contract IApiMonitorStore)
                    monitor.RecordResponse(requestId, tee.Usage?.PromptTokens,
                        tee.Usage?.CompletionTokens, tee.FirstTokenAt, tee.ResponseBody);
                    // Upstream tự đóng SSE không có [DONE]/finish_reason (vd Unsloth cancel/swap
                    // khi stream dài) — không phải lỗi mạng: client đã nhận trọn bytes như nối
                    // trực tiếp; Warning để còn dấu vết chẩn đoán server tự kết thúc stream.
                    if (tee.EndedWithoutTerminal)
                    {
                        log.Write(new LogEntry
                        {
                            Severity = LogSeverity.Warning,
                            Category = LogCategory.Request,
                            Message = $"Upstream '{provider.Name}' đóng SSE không có [DONE]/finish_reason — coi như kết thúc stream.",
                            RequestId = ClientKeyItems.RequestIdOf(ctx),
                            ClientKeyId = ClientKeyItems.IdOf(ctx),
                        });
                    }
                    LogForwarded(ctx, provider, model, response, stopwatch);
                    WarnOnUnknownToolCalls(ctx, provider, body, tee.ResponseBody);

                    if (tee.Usage is not null)
                    {
                        log.LogRequestUsage(ClientKeyItems.RequestIdOf(ctx), ClientKeyItems.IdOf(ctx),
                            tee.Usage.PromptTokens, tee.Usage.CompletionTokens);
                    }
                    else if (expectsUsage)
                    {
                        // Protocol khai báo usage luôn tồn tại (chat: inject include_usage; responses:
                        // object terminal luôn kèm usage) — upstream không trả là telemetry bất thường,
                        // không fail request; message protocol-neutral, gắn id để correlate row Debug
                        // với request (spec client-keys §6, v1-responses §6/§7)
                        log.Write(new LogEntry
                        {
                            Severity = LogSeverity.Debug,
                            Category = LogCategory.App,
                            Message = "Upstream không trả usage — counter token không tăng.",
                            RequestId = ClientKeyItems.RequestIdOf(ctx),
                            ClientKeyId = ClientKeyItems.IdOf(ctx),
                        });
                    }

                    // Gọi cho MỌI 2xx: account cần đếm request kể cả khi upstream thiếu usage;
                    // token truyền 0 trong trường hợp đó. Fail-open nằm trong sink: DB lỗi →
                    // log Error, không nổ sau khi đã stream (spec §10).
                    await usageSink.RecordAsync(ClientKeyItems.IdOf(ctx), account.Id,
                        tee.Usage?.PromptTokens ?? 0, tee.Usage?.CompletionTokens ?? 0, ct);

                    // Dataset export (spec §7.2): một lần, chỉ 2xx thắng — retry/failover không
                    // tạo bản ghi trùng. Fail-open tuyệt đối: Capture hợp đồng không ném, nhưng
                    // bọc try/catch để KHÔNG helper nào (đọc header/Find/Capture/CountSkip) được
                    // phép nổ đường forward — export hỏng thì request vẫn phục vụ bình thường.
                    if (datasetOn)
                    {
                        try
                        {
                            // aggregate = client non-stream nhưng upstream bị ép stream: AggregateSseAsync đã trả
                            // completion hoàn chỉnh trong ResponseBody, không có CompletionJson (nó không nhận
                            // captureCompletion). Lấy ResponseBody ở nhánh đó — không thì mọi request kiểu này
                            // bị sink bỏ vì CompletionJson null.
                            var completion = aggregate ? tee.ResponseBody : tee.CompletionJson;
                            var monitorRecord = monitor.Find(requestId);
                            var lane = LaneFor(provider, account);
                            // V2 §7.2: shaping là quyết định của SIDECAR (Go) — host đọc tín hiệu
                            // X-Sidecar-Shaped thay vì suy lại từ body (spec cấm duplicate logic
                            // giữa hai ngôn ngữ); header vắng (provider không qua sidecar) → false.
                            // Giữ lane guard: provider ngoài OpenCode không bao giờ báo đã đổi tools
                            // dù một middlebox nào đó lỡ dội header về.
                            var shaped = HeaderValue(response, SidecarShapedHeader);
                            var toolsRewritten = lane != DatasetLane.Plain
                                && shaped.Contains("tools", StringComparison.OrdinalIgnoreCase);
                            // Title synthesized: sidecar tự sinh completion (title request gặp 429/
                            // lỗi transport) — không phải model trả lời nên KHÔNG vào dataset train,
                            // chỉ đếm counter riêng (spec v2 §7.2).
                            if (HeaderValue(response, SidecarTitleHeader)
                                .Contains("synthesized", StringComparison.OrdinalIgnoreCase))
                            {
                                // CountSkip là method internal của sink cụ thể: DI production luôn
                                // dựng DatasetSink; sink khác (test/decorator tương lai) chỉ bị bỏ
                                // đường đếm — không được phép nổ đường forward.
                                if (dataset is DatasetSink sink)
                                {
                                    sink.CountSkip(DatasetSkipReason.Synthetic);
                                }
                            }
                            else
                            {
                                // body = body GỐC của client (tham số ForwardAsync), KHÔNG phải requestBody đã
                                // rewrite: dữ liệu train phản ánh đúng thứ client gửi — rewrite mang stream:true
                                // + include_usage do app tự thêm, đưa vào dataset là nhiễm rác (spec §7.1).
                                dataset.Capture(new DatasetCapture(requestId, protocol.Endpoint, DateTimeOffset.UtcNow, body,
                                    completion, tee.CompletionComplete, provider.Name, account.Name, model.ModelId,
                                    monitorRecord?.Model, monitorRecord?.Mode, ClientKeyItems.IdOf(ctx),
                                    tee.Usage?.PromptTokens, tee.Usage?.CompletionTokens,
                                    lane, toolsRewritten));
                            }
                        }
                        catch (Exception ex)
                        {
                            SafeDatasetLog(ex);
                        }
                    }

                    return new DispatchOutcome.Handled();
                }

                // Lỗi chưa commit (vừa nhận header) — buffer để dispatcher quyết định advance/passthrough
                var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                // Ghi cho MỌI nhánh non-2xx (retryable/fatal/passthrough) — việc monitor theo dõi
                // lỗi không phụ thuộc quyết định retry; DecodeCapped truncate BYTE trước khi decode
                // (chỉ đọc 64KB đầu, marker 1 lần — store không cắt lại, fail-open)
                monitor.RecordError(requestId, (int)response.StatusCode,
                    ApiMonitorStore.DecodeCapped(errorBody));
                var contentType = response.Content.Headers.ContentType?.ToString();
                var retryAfterRaw = response.Headers.RetryAfter?.ToString();
                if (RetryClassifier.IsRetryable(response.StatusCode))
                    return new DispatchOutcome.Retryable((int)response.StatusCode, contentType,
                        errorBody, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));

                // Journal Info chung cho Fatal lẫn Passthrough (parity quen sát 3A)
                LogForwarded(ctx, provider, model, response, stopwatch);
                var retryAfter = RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow);
                var fatal = ClassifyFatal((int)response.StatusCode, errorBody, contentType,
                    retryAfterRaw, retryAfter);
                return RefineOpencodeGate(ctx, provider, fatal, errorBody, contentType, retryAfter);
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
    /// Tinh chỉnh 403 của provider OpenCode free sau <see cref="ClassifyFatal"/> (spec
    /// opencode-free-gate-v2 §7.3) — 3 đường phân loại, chỉ áp cho host opencode.ai:
    /// <list type="bullet">
    /// <item>IP-abuse (marker egress) → <see cref="DispatchOutcome.Blocked"/>: dispatcher chặn
    /// exit proxy vừa dùng rồi thử exit khác, KHÔNG xoay account (cùng egress = cùng câu trả lời).</item>
    /// <item>Shape refusal ("within OpenCode") → nâng lên <see cref="FailoverLevel.Provider"/>
    /// (deterministic: retry account/model cùng shape là vô ích) + log Error kèm RequestId để
    /// báo hiệu gate đổi contract, cần cập nhật sidecar.</item>
    /// <item>Còn lại → giữ nguyên outcome của <see cref="ClassifyFatal"/>.</item>
    /// </list>
    /// </summary>
    private DispatchOutcome RefineOpencodeGate(HttpContext ctx, Provider provider,
        DispatchOutcome outcome, byte[] errorBody, string? contentType, TimeSpan? retryAfter)
    {
        if (!OpenCodeFreeFingerprint.IsApplicable(provider)
            || outcome is not DispatchOutcome.Fatal { Status: 403 })
        {
            return outcome;
        }

        if (OpencodeGateClassifier.IsIpAbuse(errorBody))
        {
            return new DispatchOutcome.Blocked(403, contentType, errorBody);
        }

        if (OpencodeGateClassifier.IsShapeRefusal(errorBody))
        {
            // Error (không phải Warn): gate đổi contract = shaping của sidecar cần cập nhật,
            // không phải lỗi tạm thời; RequestId để correlate ngay trong log người dùng.
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Error,
                Category = LogCategory.Request,
                Message = "OpenCode từ chối shape request (403 within OpenCode) — gate đã đổi "
                    + "contract; kiểm tra/cập nhật shaping của sidecar.",
                Details = ErrorBodySnippet(errorBody),
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
            return new DispatchOutcome.Fatal(FailoverLevel.Provider, 403, contentType, errorBody,
                retryAfter);
        }

        return outcome;
    }

    /// <summary>Snippet body lỗi cho <c>Details</c> log (≤500 ký tự, best-effort UTF-8).</summary>
    private static string ErrorBodySnippet(byte[] errorBody)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(errorBody);
            return text.Length <= 500 ? text : text[..500];
        }
        catch
        {
            // Decode lỗi (best-effort) — bỏ snippet, message vẫn đầy đủ
            return string.Empty;
        }
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

    // Route CẤU HÌNH + down-state, không phải hop thật: ProxyContext.Current đã bị reset sau khi
    // nhận header nên hop không còn truy được (spec §4.1/D3). Endpoint không chứa credentials.
    private string DescribeProxyRoute(Provider provider, ProviderAccount account)
    {
        var selection = proxySelectionResolver.Resolve(provider, account);
        if (selection.IsDirect)
            return "direct";

        var ids = selection.ProxyIds!;
        var live = proxyPool.Snapshot().Where(s => ids.Contains(s.Id)).ToList();
        var endpoints = string.Join(", ", live.Select(s => $"{s.Id}:{s.Endpoint}{(s.IsDown ? "(down)" : "")}"));
        return $"{selection.Mode} ids=[{string.Join(",", ids)}] enabled=[{endpoints}]";
    }

    // Tín hiệu CHẨN ĐOÁN (không đổi response/outcome): model gọi tên tool client KHÔNG khai báo
    // trong body gốc — với mọi provider, client sẽ báo "invalid tool" rất khó hiểu khi debug.
    // Trên lane OpenCode free, nguyên nhân thường là official tool không có bản tương ứng phía
    // client vì sidecar (Go) đã alias-restore tên tool trong response. Ghi Warning kèm RequestId
    // để correlate ngay trong log; mỗi tên chỉ cảnh báo MỘT lần (không spam theo chunk/occurrence).
    private void WarnOnUnknownToolCalls(HttpContext ctx, Provider provider, byte[] body,
        string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody)) return;

        foreach (var name in UnknownToolCalls(responseBody, DeclaredToolNames(body)))
        {
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategory.Request,
                Message = $"Model gọi tool '{name}' không có trong danh sách tools client khai báo "
                          + $"(provider '{provider.Name}') — client sẽ báo lỗi invalid tool khi "
                          + "nhận tên này; trên lane OpenCode free, đây thường là official tool "
                          + "không có bản tương ứng phía client (sidecar đã alias-restore tên "
                          + "tool trong response).",
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
        }
    }

    /// <summary>Tên tool chứa <c>tool_calls</c> trong chat choice: stream (<c>delta</c>) và non-stream (<c>message</c>).</summary>
    private static readonly string[] ToolCallHolderKeys = ["delta", "message"];

    /// <summary>
    /// Tên tool client khai báo trong body chat GỐC (<c>tools[].function.name</c>) — body
    /// hỏng/không phải JSON object → tập rỗng (chẩn đoán fail-open, không ném trên đường forward).
    /// </summary>
    private static HashSet<string> DeclaredToolNames(byte[] body)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
            {
                return declared;
            }

            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object
                    && tool.TryGetProperty("function", out var function)
                    && function.ValueKind == JsonValueKind.Object
                    && function.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { } value)
                {
                    declared.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // body hỏng: coi như client không khai báo tool nào — chỉ là tín hiệu chẩn đoán
        }
        return declared;
    }

    /// <summary>
    /// Tên tool model ĐÃ gọi trong <paramref name="responseBody"/> nhưng client không khai báo —
    /// walk <c>choices[].{delta,message}.tool_calls[].function.name</c> của mọi dòng JSON
    /// (<paramref name="responseBody"/> là payload SSE ghép dòng hoặc JSON gộp của client
    /// non-stream); mỗi tên trả về tối đa MỘT lần. Dòng hỏng/chunk cắt bị bỏ qua — chỉ là tín
    /// hiệu chẩn đoán, không bao giờ ném.
    /// </summary>
    private static List<string> UnknownToolCalls(string responseBody, HashSet<string> declared)
    {
        var unknown = new List<string>();
        foreach (var rawLine in responseBody.Split('\n'))
        {
            var text = rawLine.Trim();
            if (text.Length == 0) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                continue; // dòng hỏng (chunk cắt giữa JSON) — bỏ qua
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object) continue;
                    foreach (var holderKey in ToolCallHolderKeys)
                    {
                        if (!choice.TryGetProperty(holderKey, out var holder)
                            || holder.ValueKind != JsonValueKind.Object
                            || !holder.TryGetProperty("tool_calls", out var calls)
                            || calls.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var call in calls.EnumerateArray())
                        {
                            if (call.ValueKind == JsonValueKind.Object
                                && call.TryGetProperty("function", out var function)
                                && function.ValueKind == JsonValueKind.Object
                                && function.TryGetProperty("name", out var name)
                                && name.ValueKind == JsonValueKind.String
                                && name.GetString() is { } value
                                && !declared.Contains(value)
                                && !unknown.Contains(value))
                            {
                                unknown.Add(value);
                            }
                        }
                    }
                }
            }
        }
        return unknown;
    }

    /// <summary>
    /// Làn provenance của record dataset (spec §7.4): <see cref="DatasetLane.Plain"/> khi provider
    /// không thuộc lane OpenCode (không qua sidecar); <see cref="DatasetLane.FreeFingerprint"/>
    /// khi account key rỗng (free lane — sidecar shaping), còn lại là
    /// <see cref="DatasetLane.KeyedFingerprint"/> (lane keyed passthrough).
    /// </summary>
    private static DatasetLane LaneFor(Provider provider, ProviderAccount account) =>
        !OpenCodeFreeFingerprint.IsApplicable(provider)
            ? DatasetLane.Plain
            : string.IsNullOrEmpty(account.ApiKeyEncrypted)
                ? DatasetLane.FreeFingerprint
                : DatasetLane.KeyedFingerprint;

    // Fail-open của export: ngay cả log lỗi cũng không được nổ ra khỏi ForwardAsync
    // (pattern ApiMonitorStore.SafeLog — chính log nổ cũng phải nuốt).
    private void SafeDatasetLog(Exception ex)
    {
        try
        {
            log.Error("ProxyRequestHandler: dataset capture failed", ex);
        }
        catch
        {
            // intentional swallow — không còn chỗ report nào an toàn hơn (mirror TraceFeed.Publish)
        }
    }

    // Tín hiệu sidecar → host (spec v2 §5.7): custom response header, nằm ở response.Headers
    // (KHÔNG phải response.Content.Headers). X-Sidecar-Shaped: "tools+prompt" khi free lane đã
    // shaping / "none" (title-gen, lane keyed); X-Sidecar-Title: "synthesized" chỉ khi sidecar
    // tự sinh completion. Vắng cả hai = provider không qua sidecar.
    private const string SidecarShapedHeader = "X-Sidecar-Shaped";
    private const string SidecarTitleHeader = "X-Sidecar-Title";

    /// <summary>
    /// Giá trị header tín hiệu sidecar dạng chuỗi — vắng → rỗng; join nhiều giá trị phòng
    /// trường hợp trung gian tách header (sidecar thật chỉ gửi một giá trị).
    /// </summary>
    private static string HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

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
