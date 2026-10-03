using System.Globalization;
using Microsoft.Extensions.Hosting;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Queue-first dispatcher (spec §2.1): chờ event → Peek → resolve → chọn → TryEnter → Take
/// atomic → serve song song. Thiếu capacity: KHÔNG Take (item ở lại queue — park vô hạn),
/// chờ <see cref="IRequestQueue.Changed"/> | <see cref="IExecutionList.Exited"/> đánh thức.
/// </summary>
public sealed class DispatcherLoop(
    IRequestQueue queue,
    IExecutionList executions,
    IComboResolver resolver,
    IModelSelector selector,
    ChatCompletionsHandler handler,
    ILogService log,
    IModelHealthStore health) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe TRƯỚC lần drain đầu — event xảy ra giữa "queue rỗng" và "chờ" không bị mất
        queue.Changed += OnWake;
        executions.Exited += OnWake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (await TryDispatchOnceAsync(stoppingToken))
                    {
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Vòng lặp phải sống: item đầu giữ nguyên, event sau sẽ retry (spec §4).
                    // try/catch INLINE (không delegate): log (SQLite) ném không được thoát ExecuteAsync —
                    // loop chết = không còn dispatcher nào serve. "log không được làm hỏng request path" (I2)
                    try
                    {
                        log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.App);
                    }
                    catch
                    {
                        // Nuốt chủ đích: backend log lỗi không được giết dispatcher loop
                    }
                }

                try
                {
                    await _wake.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            queue.Changed -= OnWake;
            executions.Exited -= OnWake;
        }
    }

    /// <summary>Event handler — chỉ Release, không làm gì đồng bộ (bắn từ thread khác).</summary>
    private void OnWake() => _wake.Release();

    /// <summary>Một vòng dispatch. Trả <see langword="true"/> nếu có tiến triển (drain tiếp), false = park/rỗng → chờ event.</summary>
    private async Task<bool> TryDispatchOnceAsync(CancellationToken ct)
    {
        if (!queue.Peek(out var request))
            return false;

        var selection = await resolver.ResolveAsync(request.Model, ct);
        if (selection is SelectionFailure failure)
        {
            // Resolve fail = xong xét — gỡ khỏi queue rồi báo outcome để endpoint ghi lỗi.
            // Log bọc INLINE: log ném (SQLite) KHÔNG được chặn TrySetResult — item đã rời queue
            // mà không outcome = endpoint chờ vĩnh viễn, cancel API trả 404 (I2).
            // GIỮ NGUYÊN thứ tự log → TrySetResult: outcome báo TRƯỚC khi log xong sẽ để endpoint
            // hoàn tất + test xoá file DB trong lúc log còn ghi → IOException.
            if (!queue.TryRemove(request.Id, out _))
                return true; // vừa bị cancel/abort gỡ — bên kia đã báo outcome rồi
            try
            {
                LogResolveFailure(failure);
            }
            catch
            {
                // Nuốt chủ đích: "log không được làm hỏng request path"
            }
            request.Completion.TrySetResult(CreateResolveError(failure));
            return true;
        }

        var success = (SelectionSuccess)selection;

        // Walk 3C: bỏ candidate model đang ManualRetry + candidate đã thử trong request này (§3.2/§3.4)
        var remaining = FilterRemaining(success.Candidates, request);
        if (remaining.Count == 0)
        {
            if (!queue.TryRemove(request.Id, out _))
                return true; // vừa bị cancel/abort gỡ — bên kia đã báo outcome rồi
            request.Completion.TrySetResult(request.Retry.HasTried
                ? CompleteExhaustion(request)
                : new DispatchOutcome.Error(503,
                    $"The model '{request.Model}' is temporarily unavailable",
                    "server_error", null, null));
            return true;
        }

        var candidate = await selector.TrySelectAsync(
            new SelectionSuccess(remaining, success.Mode), ct);
        if (candidate is null)
            return false; // park — item KHÔNG bị Take, chờ Changed|Exited

        var accountId = await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
            candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
            request.EnqueuedAt, ct);
        if (accountId is null)
            return false; // capacity vừa hết (mọi TK đầy) — park, Exited sẽ đánh thức

        if (!queue.Take(request.Id, out var taken))
        {
            // Take fail = item vừa bị huỷ/abort giữa TryEnter và Take — trả slot ngay (spec §3.3)
            executions.Exit(request.Id);
            return true;
        }

        _ = ServeAsync(taken, candidate, remaining, success.Mode, accountId.Value);
        return true;
    }

    /// <summary>
    /// Serve 1 request qua vòng walk: mỗi candidate 1 lần, Retryable → Exit + advance kế;
    /// hết list → RecordExhaustion + Passthrough/Error502. Fire-and-forget từ
    /// <see cref="TryDispatchOnceAsync"/> — không block dispatcher loop.
    /// </summary>
    private async Task ServeAsync(ProxyRequest request, ModelCandidate candidate,
        IReadOnlyList<ModelCandidate> remaining, ComboMode mode, long accountId)
    {
        while (true)
        {
            DispatchOutcome outcome;
            try
            {
                // RequestAborted của client — disconnect giữa chừng cắt stream, không phải lỗi upstream (3A)
                outcome = await handler.ForwardAsync(request.Context, candidate.Provider,
                    candidate.Model, request.Body, accountId, request.Context.RequestAborted);
            }
            catch (OperationCanceledException) when (request.Context.RequestAborted.IsCancellationRequested)
            {
                outcome = new DispatchOutcome.Aborted(); // client ngắt giữa serve — không 502 (3A parity)
            }
            catch (Exception ex)
            {
                // Response đã commit (stream giữa chừng) → Aborted: endpoint KHÔNG append JSON 500 vào stream (I1).
                // Log bọc INLINE: nếu ném, outcome sẽ không bao giờ ghi → endpoint treo vĩnh viễn (I2)
                try
                {
                    log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
                }
                catch
                {
                    // Nuốt chủ đích: "log không được làm hỏng request path"
                }
                outcome = request.Context.Response.HasStarted
                    ? new DispatchOutcome.Aborted()
                    : new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null);
            }

            if (outcome is DispatchOutcome.Retryable retryable)
            {
                request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
                request.Retry.LastRetryable = retryable;

                var next = FilterRemaining(remaining, request);
                if (next.Count == 0)
                {
                    // RecordExhaustion TRƯỚC Exit — fuse phải mở trước khi Exited wake dispatch
                    // request đang queue, nếu không request kế sẽ gọi tiếp vào model vừa chết (§3.4)
                    var exhausted = CompleteExhaustion(request);
                    executions.Exit(request.Id);
                    request.Completion.TrySetResult(exhausted);
                    return;
                }

                LogAdvance(request, candidate, retryable);
                // Exit TRƯỚC khi advance — trả slot ngay, không giữ trong lúc chọn candidate kế
                executions.Exit(request.Id);

                ModelCandidate? nextCandidate;
                long? nextAccountId = null;
                try
                {
                    nextCandidate = await selector.TrySelectAsync(
                        new SelectionSuccess(next, mode), request.Context.RequestAborted);
                    nextAccountId = nextCandidate is null
                        ? null
                        : await executions.TryEnterAsync(nextCandidate.Provider.Id, request.Id,
                            nextCandidate.Provider.Name, nextCandidate.Model.ModelId,
                            request.Priority, request.EnqueuedAt, request.Context.RequestAborted);
                    if (nextCandidate is not null && nextAccountId is null)
                        nextCandidate = null; // capacity corner — park lại, TK đã trả qua Exit
                }
                catch (OperationCanceledException) when (request.Context.RequestAborted.IsCancellationRequested)
                {
                    // Exit đã xong trước advance — chỉ cần báo outcome (idempotent TCS)
                    request.Completion.TrySetResult(new DispatchOutcome.Aborted());
                    return;
                }
                catch (Exception ex)
                {
                    // Slot đã trả — lỗi select/enter phải thành outcome, không được nuốt (I2)
                    try
                    {
                        log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
                    }
                    catch
                    {
                        // Nuốt chủ đích: "log không được làm hỏng request path"
                    }
                    request.Completion.TrySetResult(request.Context.Response.HasStarted
                        ? new DispatchOutcome.Aborted()
                        : new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null));
                    return;
                }

                if (nextCandidate is null)
                {
                    ReenqueueForPark(request);
                    return;
                }

                candidate = nextCandidate;
                remaining = next;
                accountId = nextAccountId!.Value; // nextCandidate != null ⇒ đã enter thành công (TK mới mỗi vòng — D-B6)
                continue;
            }

            // Không phải Retryable: trả slot rồi complete (Handled/Passthrough/Error/Cancelled/Aborted)
            executions.Exit(request.Id);
            if (outcome is DispatchOutcome.Handled)
            {
                try
                {
                    health.RecordSuccess(candidate.Model.ModelId); // 2xx reset counter (§3.4)
                }
                catch
                {
                    // Nuốt chủ đích: store lỗi không được chặn outcome (I2)
                }
            }
            request.Completion.TrySetResult(outcome);
            return;
        }
    }

    /// <summary>Payload lỗi resolve — GIỮ NGUYÊN message/type/param/code 3A, chỉ đổi nơi gọi (spec §4).</summary>
    private static DispatchOutcome.Error CreateResolveError(SelectionFailure failure) =>
        failure.Reason == ResolveFailure.NotFound
            ? new(404, $"The model '{failure.ModelId}' does not exist",
                "invalid_request_error", "model", "model_not_found")
            : new(503, $"The model '{failure.ModelId}' is not supported yet",
                "server_error", null, null);

    private void LogResolveFailure(SelectionFailure failure)
    {
        if (failure.Reason == ResolveFailure.NotFound)
            log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
        else
            log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                LogCategory.Request);
    }

    /// <summary>Candidate chưa thử + model chưa ManualRetry — dùng cho dispatch đầu và mỗi bước walk (§3.2/§3.4).</summary>
    private IReadOnlyList<ModelCandidate> FilterRemaining(IReadOnlyList<ModelCandidate> candidates,
        ProxyRequest request) =>
        candidates
            .Where(c => !health.IsManualRetry(c.Model.ModelId)
                && !request.Retry.IsTried(c.Provider.Id, c.Model.ModelId))
            .ToList();

    /// <summary>Ghi failure từng distinct model đã thử rồi trả outcome exhaustion — gọi 2 nơi (§3.3/§3.4).</summary>
    private DispatchOutcome CompleteExhaustion(ProxyRequest request)
    {
        RecordExhaustion(request);
        return ExhaustionOutcome(request);
    }

    /// <summary>
    /// +1/exhaustion cho từng distinct model đã thử (Quyết định #4) + log Error exhaustion.
    /// Mọi chỗ gọi bọc try — log/store ném không được chặn TrySetResult (I2).
    /// </summary>
    private void RecordExhaustion(ProxyRequest request)
    {
        var retryAfter = request.Retry.LastRetryable?.RetryAfter;
        foreach (var modelId in request.Retry.TriedModels)
        {
            try
            {
                health.RecordFailure(modelId, retryAfter);
            }
            catch
            {
                // Nuốt chủ đích: store lỗi không được phá outcome
            }
        }
        try
        {
            log.Error(
                $"Request {request.Id} thất bại sau {request.Retry.TriedCount} candidate — " +
                "chuyển phản hồi cuối về client", category: LogCategory.Request);
        }
        catch
        {
            // Nuốt chủ đích: log không được phá outcome
        }
    }

    /// <summary>Exhaustion contract §3.3: attempt cuối có HTTP → Passthrough nguyên; mạng → Error 502 (y như 3A).</summary>
    private static DispatchOutcome ExhaustionOutcome(ProxyRequest request)
    {
        var last = request.Retry.LastRetryable;
        return last?.Status is { } status
            ? new DispatchOutcome.Passthrough(status, last.ContentType, last.Body,
                FormatRetryAfter(last.RetryAfter))
            : new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error",
                null, null);
    }

    /// <summary>Log Warn advance failover — chỉ khi thật sự còn candidate kế (spec §5); bọc nuốt (I2).</summary>
    private void LogAdvance(ProxyRequest request, ModelCandidate failed,
        DispatchOutcome.Retryable retryable)
    {
        var reason = retryable.Status is { } status ? $"HTTP {status}" : "lỗi mạng";
        try
        {
            log.Warn(
                $"Chuyển candidate kế: '{failed.Provider.Name}'/'{failed.Model.ModelId}' " +
                $"lỗi retryable ({reason}) — request {request.Id}", LogCategory.Request);
        }
        catch
        {
            // Nuốt chủ đích: log không được chặn walk
        }
    }

    /// <summary>
    /// Capacity corner sau advance: re-enqueue vào queue chờ <c>Exited</c>/<c>Changed</c> (park 3B)
    /// — KHÔNG phải requeue-vì-lỗi: candidate đã thử vẫn bị exclude qua <see cref="RetryState"/> (§3.2).
    /// </summary>
    private void ReenqueueForPark(ProxyRequest request)
    {
        if (request.Context.RequestAborted.IsCancellationRequested)
        {
            request.Completion.TrySetResult(new DispatchOutcome.Aborted());
            return;
        }
        if (!queue.Enqueue(request))
        {
            // Id đã có trong queue (rare) — không được nuốt im lặng, outcome luôn phải tới endpoint
            try
            {
                log.Error($"Không enqueue lại được request {request.Id} khi chờ slot.",
                    category: LogCategory.Request);
            }
            catch
            {
                // Nuốt chủ đích: log không được phá outcome
            }
            request.Completion.TrySetResult(new DispatchOutcome.Error(500, "Internal server error",
                "server_error", null, null));
            return;
        }
        // Token cancel giữa check trên và Enqueue: callback Register (endpoint) đã lỡ fire khi
        // item chưa trong queue → tự gỡ lại + Cancelled (spec §3.4); TrySetResult idempotent
        if (request.Context.RequestAborted.IsCancellationRequested
            && queue.TryRemove(request.Id, out _))
        {
            request.Completion.TrySetResult(new DispatchOutcome.Cancelled());
        }
    }

    /// <summary>TimeSpan → chuỗi delta-seconds (InvariantCulture, ceiling) cho header Retry-After.</summary>
    private static string? FormatRetryAfter(TimeSpan? retryAfter) =>
        retryAfter is { } value
            ? ((int)Math.Ceiling(value.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture)
            : null;
}
