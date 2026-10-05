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
    ILogService log) : BackgroundService
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

        // Walk exhaustive-failover §3.2: filter → select → TryEnter với exclude = TK đã thử
        // của provider đang xét. NoAccountLeft (snapshot stale — TK bị tắt giữa chừng) →
        // MarkTried(pair) rồi lọc lại; mỗi vòng đánh dấu 1 pair nên hữu hạn.
        ModelCandidate? candidate = null;
        IReadOnlyList<ModelCandidate> remaining = [];
        long accountId;
        while (true)
        {
            remaining = FilterRemaining(success.Candidates, request);
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

            candidate = await selector.TrySelectAsync(
                new SelectionSuccess(remaining, success.Mode), ct);
            if (candidate is null)
                return false; // park — item KHÔNG bị Take, chờ Changed|Exited

            var enter = await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
                candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
                request.EnqueuedAt, TriedAccountsOf(candidate, request.Retry), ct);
            if (enter is TryEnterResult.Entered entered)
            {
                accountId = entered.AccountId;
                break;
            }
            if (enter is TryEnterResult.Full)
                return false; // capacity hết → park, Exited/Changed đánh thức

            // NoAccountLeft: mọi TK enabled của provider này đã thử nhưng snapshot còn ghi
            // chưa (stale) — đánh dấu pair để filter loại, rồi chọn lại (hữu hạn)
            request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
        }

        if (!queue.Take(request.Id, out var taken))
        {
            // Take fail = item vừa bị huỷ/abort giữa TryEnter và Take — trả slot ngay (giữ hành vi 3B)
            executions.Exit(request.Id);
            return true;
        }

        _ = ServeAsync(taken, candidate, remaining, success.Mode, accountId);
        return true;
    }

    /// <summary>
    /// Serve 1 request qua vòng walk exhaustive (spec exhaustive-failover §2.4/§3.2): mỗi attempt
    /// fail ghi journal rồi rẽ theo cấp — Account (Retryable 429/408/5xx + Fatal 401/403) →
    /// <c>MarkAccountTried</c> + <c>TryEnter</c> TK kế cùng provider; Model/Provider → đánh dấu
    /// rồi candidate-advance; hết list → <see cref="CompleteExhaustion"/> (Passthrough/Error502).
    /// Fire-and-forget từ <see cref="TryDispatchOnceAsync"/> — không block dispatcher loop.
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

            if (outcome is not (DispatchOutcome.Retryable or DispatchOutcome.Fatal))
            {
                // Không phải Retryable/Fatal: trả slot rồi complete (Handled/Passthrough/Error/Cancelled/Aborted)
                executions.Exit(request.Id);
                request.Completion.TrySetResult(outcome);
                return;
            }

            var status = outcome switch
            {
                DispatchOutcome.Retryable r => r.Status,
                DispatchOutcome.Fatal f => f.Status,
                _ => null,
            };
            // Journal attempt (spec exhaustive-failover §2.4) — Task 6 format log từ Attempts/Trail;
            // accountName tra từ snapshot theo accountId, không có (sentinel 0/TK xóa) → "-"
            request.Retry.RecordAttempt(candidate.Provider.Name, candidate.Model.ModelId,
                AccountNameOf(candidate, accountId), status);
            // LastFailure nhận từ CẢ 2 outcome — attempt cuối quyết định exhaustion (spec exhaustive-failover §2.3)
            request.Retry.LastFailure = outcome switch
            {
                DispatchOutcome.Retryable r =>
                    new RetryState.Failure(r.Status, r.ContentType, r.Body, r.RetryAfter),
                DispatchOutcome.Fatal f =>
                    new RetryState.Failure(f.Status, f.ContentType, f.Body, f.RetryAfter),
                _ => request.Retry.LastFailure,
            };

            // Retryable (429/408/5xx) dispatcher gán cấp Account (spec exhaustive-failover §3.1)
            var level = outcome is DispatchOutcome.Fatal fatal
                ? fatal.Level
                : FailoverLevel.Account;

            if (level == FailoverLevel.Account)
            {
                request.Retry.MarkAccountTried(candidate.Provider.Id, accountId);
                // Exit TRƯỚC TryEnter — trả slot TK cũ, TK kế reserve lại trong cùng request (§2.4)
                executions.Exit(request.Id);
                TryEnterResult enter;
                try
                {
                    enter = await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
                        candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
                        request.EnqueuedAt, TriedAccountsOf(candidate, request.Retry),
                        request.Context.RequestAborted);
                }
                catch (OperationCanceledException) when (request.Context.RequestAborted.IsCancellationRequested)
                {
                    // Slot đã trả — chỉ cần báo outcome (idempotent TCS)
                    request.Completion.TrySetResult(new DispatchOutcome.Aborted());
                    return;
                }
                catch (Exception ex)
                {
                    // Slot đã trả — lỗi enter phải thành outcome, không được nuốt (I2)
                    request.Completion.TrySetResult(AdvanceFailureOutcome(request, ex));
                    return;
                }

                switch (enter)
                {
                    case TryEnterResult.Entered entered:
                        accountId = entered.AccountId; // cùng candidate — tiếp vòng serve với TK kế
                        continue;
                    case TryEnterResult.Full:
                        // TK chưa thử còn đó nhưng đầy → park chờ Exited; KHÔNG MarkTried pair
                        ReenqueueForPark(request);
                        return;
                    default: // NoAccountLeft — mọi TK enabled của provider này đã thử
                        request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
                        break; // rơi xuống candidate-advance
                }
            }
            else
            {
                if (level == FailoverLevel.Provider)
                {
                    // Lỗi provider-wide (mạng/404 khác) — filter loại MỌI candidate của provider,
                    // kể cả model chưa thử (§2.3): dispatch lại không tốn timeout thử model khác
                    request.Retry.MarkProviderFailed(candidate.Provider.Id);
                }
                request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
                executions.Exit(request.Id);
            }

            // — Candidate-advance (spec exhaustive-failover §2.4) —
            var next = FilterRemaining(remaining, request);
            if (next.Count == 0)
            {
                // Exit đã làm ở nhánh trên (idempotent) — CompleteExhaustion TRƯỚC khi complete
                // để log exhaustion ghi xong rồi mới endpoint nhận outcome (I2)
                var exhausted = CompleteExhaustion(request);
                executions.Exit(request.Id);
                request.Completion.TrySetResult(exhausted);
                return;
            }
            LogAdvance(request, candidate, status);

            // Vòng trong: candidate kế trả NoAccountLeft (snapshot stale) → MarkTried(pair) →
            // lọc lại; mỗi vòng 1 pair nên hữu hạn, không loop vô hạn
            while (true)
            {
                ModelCandidate? nextCandidate;
                long? nextAccountId = null;
                try
                {
                    nextCandidate = await selector.TrySelectAsync(
                        new SelectionSuccess(next, mode), request.Context.RequestAborted);
                    if (nextCandidate is not null)
                    {
                        var enter = await executions.TryEnterAsync(nextCandidate.Provider.Id,
                            request.Id, nextCandidate.Provider.Name, nextCandidate.Model.ModelId,
                            request.Priority, request.EnqueuedAt,
                            TriedAccountsOf(nextCandidate, request.Retry),
                            request.Context.RequestAborted);
                        switch (enter)
                        {
                            case TryEnterResult.Entered entered:
                                nextAccountId = entered.AccountId;
                                break;
                            case TryEnterResult.Full:
                                nextCandidate = null; // capacity corner — park (khối dưới)
                                break;
                            default: // NoAccountLeft — đánh dấu pair rồi lọc lại (hữu hạn)
                                request.Retry.MarkTried(nextCandidate.Provider.Id,
                                    nextCandidate.Model.ModelId);
                                next = FilterRemaining(next, request);
                                if (next.Count == 0)
                                {
                                    var exhaustedLoop = CompleteExhaustion(request);
                                    request.Completion.TrySetResult(exhaustedLoop);
                                    return;
                                }
                                continue; // vòng trong — chọn candidate khác
                        }
                    }
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
                    request.Completion.TrySetResult(AdvanceFailureOutcome(request, ex));
                    return;
                }

                if (nextCandidate is null)
                {
                    // Selector null (mọi candidate kẹt capacity) hoặc TryEnter Full — park lại,
                    // TK đã trả qua Exit
                    ReenqueueForPark(request);
                    return;
                }

                candidate = nextCandidate;
                remaining = next;
                accountId = nextAccountId!.Value; // Entered ⇒ nextAccountId gán (TK mới mỗi vòng — D-B6)
                break; // ra vòng ngoài — forward attempt kế
            }
        }
    }

    /// <summary>
    /// Lỗi exception giữa select/enter sau khi slot đã trả — log Error (bọc nuốt I2) rồi trả
    /// outcome: response đã stream → <see cref="DispatchOutcome.Aborted"/>, chưa → Error 500.
    /// </summary>
    private DispatchOutcome AdvanceFailureOutcome(ProxyRequest request, Exception ex)
    {
        try
        {
            log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
        }
        catch
        {
            // Nuốt chủ đích: "log không được làm hỏng request path"
        }
        return request.Context.Response.HasStarted
            ? new DispatchOutcome.Aborted()
            : new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null);
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

    /// <summary>
    /// Candidate chưa thử trong request này + chưa fail cấp Provider + còn TK enabled chưa thử
    /// — dùng cho dispatch đầu và mỗi bước walk (spec exhaustive-failover §3.2).
    /// </summary>
    private IReadOnlyList<ModelCandidate> FilterRemaining(IReadOnlyList<ModelCandidate> candidates,
        ProxyRequest request) =>
        candidates
            .Where(c => !request.Retry.IsTried(c.Provider.Id, c.Model.ModelId)
                && !request.Retry.IsProviderFailed(c.Provider.Id)
                && HasEnabledUntriedAccount(c, request.Retry))
            .ToList();

    /// <summary>
    /// Còn TK enabled CHƯA thử (spec exhaustive-failover §3.2): nav Accounts chưa load (null)
    /// → không loại ở đây — ExecutionList tự filter sau materialize; mọi TK disabled hoặc đã
    /// thử trong request này → loại candidate (giữ lại thì TryEnter tạo sentinel 0 forward 503
    /// thay vì chọn TK healthy).
    /// </summary>
    private static bool HasEnabledUntriedAccount(ModelCandidate candidate, RetryState retry) =>
        candidate.Provider.Accounts?
            .Any(a => a.Enabled && !retry.IsAccountTried(candidate.Provider.Id, a.Id)) != false;

    /// <summary>
    /// Tên TK đã dùng cho journal attempt — tra từ snapshot theo <paramref name="accountId"/>;
    /// không có (sentinel 0 / TK bị xóa giữa chừng) → "-".
    /// </summary>
    private static string AccountNameOf(ModelCandidate candidate, long accountId) =>
        candidate.Provider.Accounts?.FirstOrDefault(a => a.Id == accountId)?.Name ?? "-";

    /// <summary>
    /// Tập TK của provider <paramref name="candidate"/> đã thử trong request — truyền vào
    /// <c>TryEnter</c> làm exclude (spec exhaustive-failover §2.2): scope theo provider nên
    /// TK của provider khác không bị loại oan.
    /// </summary>
    private static IReadOnlySet<long> TriedAccountsOf(ModelCandidate candidate, RetryState retry)
    {
        HashSet<long> tried = [];
        if (candidate.Provider.Accounts is { } accounts)
        {
            foreach (var account in accounts)
            {
                if (retry.IsAccountTried(candidate.Provider.Id, account.Id))
                    tried.Add(account.Id);
            }
        }
        return tried;
    }

    /// <summary>Ghi log Error exhaustion rồi trả outcome — gọi 2 nơi (spec exhaustive-failover §4).</summary>
    private DispatchOutcome CompleteExhaustion(ProxyRequest request)
    {
        RecordExhaustion(request);
        return ExhaustionOutcome(request);
    }

    /// <summary>
    /// Log Error exhaustion (§5) — không còn record failure theo model (circuit đã bỏ).
    /// Bọc try: log ném không được chặn TrySetResult (I2).
    /// </summary>
    private void RecordExhaustion(ProxyRequest request)
    {
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

    /// <summary>Exhaustion contract (spec exhaustive-failover §4): attempt cuối có HTTP → Passthrough nguyên; mạng → Error 502 (y như 3A).</summary>
    private static DispatchOutcome ExhaustionOutcome(ProxyRequest request)
    {
        var last = request.Retry.LastFailure;
        return last?.Status is { } status
            ? new DispatchOutcome.Passthrough(status, last.ContentType, last.Body,
                FormatRetryAfter(last.RetryAfter))
            : new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error",
                null, null);
    }

    /// <summary>Log Warn advance failover — chỉ khi thật sự còn candidate kế (spec §5); bọc nuốt (I2).</summary>
    private void LogAdvance(ProxyRequest request, ModelCandidate failed, int? status)
    {
        var reason = status is { } code ? $"HTTP {code}" : "lỗi mạng";
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
    /// Capacity corner (selector null / TryEnter Full): re-enqueue vào queue chờ <c>Exited</c>/<c>Changed</c>
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
        // item chưa trong queue → tự gỡ lại + Cancelled (giữ hành vi cancel 3B); TrySetResult idempotent
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
