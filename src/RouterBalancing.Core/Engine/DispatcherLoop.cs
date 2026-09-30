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
        var candidate = await selector.TrySelectAsync(success, ct);
        if (candidate is null)
            return false; // park — item KHÔNG bị Take, chờ Changed|Exited

        if (!await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
                candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
                request.EnqueuedAt, ct))
            return false; // capacity vừa hết — park, Exited sẽ đánh thức

        if (!queue.Take(request.Id, out var taken))
        {
            // Take fail = item vừa bị huỷ/abort giữa TryEnter và Take — trả slot ngay (spec §3.3)
            executions.Exit(request.Id);
            return true;
        }

        _ = ServeAsync(taken, candidate);
        return true;
    }

    private async Task ServeAsync(ProxyRequest request, ModelCandidate candidate)
    {
        DispatchOutcome outcome;
        try
        {
            // RequestAborted của client — disconnect giữa chừng cắt stream, không phải lỗi upstream (3A)
            outcome = await handler.ForwardAsync(request.Context, candidate.Provider,
                candidate.Model, request.Body, request.Context.RequestAborted);
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
        finally
        {
            // Exit TRƯỚC khi báo outcome — cancel endpoint kiểm tra Contains thấy đúng ngay (spec §2.3)
            executions.Exit(request.Id);
        }

        // Dispatcher là nơi duy nhất convert Retryable — endpoint chỉ thấy Passthrough/Error
        // (spec §2.2). T5 sẽ thay nhánh này bằng walk (advance) + RecordExhaustion.
        if (outcome is DispatchOutcome.Retryable retryable)
            outcome = ConvertRetryable(retryable);

        request.Completion.TrySetResult(outcome);
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
    /// Retryable → outcome endpoint ghi được: có HTTP response → Passthrough (client thấy
    /// đúng response cuối như 3A); lỗi mạng → Error 502 (exhaustion contract §3.3).
    /// </summary>
    private static DispatchOutcome ConvertRetryable(DispatchOutcome.Retryable retryable) =>
        retryable.Status is { } status
            ? new DispatchOutcome.Passthrough(status, retryable.ContentType, retryable.Body,
                FormatRetryAfter(retryable.RetryAfter))
            : new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error",
                null, null);

    /// <summary>TimeSpan → chuỗi delta-seconds (InvariantCulture, ceiling) cho header Retry-After.</summary>
    private static string? FormatRetryAfter(TimeSpan? retryAfter) =>
        retryAfter is { } value
            ? ((int)Math.Ceiling(value.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture)
            : null;
}
