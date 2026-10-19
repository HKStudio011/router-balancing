using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyUsageSink"/>
public sealed class ClientKeyUsageSink(
    IClientKeyService keys, IClientKeyRateLimiter limiter, ILogService log) : IClientKeyUsageSink
{
    public async Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
        CancellationToken ct = default)
    {
        if (clientKeyId is not long id) return;

        // TPM window là gate thật (in-memory) — cộng TRƯỚC, không phụ thuộc DB (spec §5/§10)
        limiter.AddTokens(id, promptTokens + completionTokens);

        try
        {
            await keys.RecordTokensAsync(id, promptTokens, completionTokens, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client ngắt đúng lúc ghi (race sau khi upstream đã trả xong): request không còn
            // người nhận, DB hủy theo token — không phải lỗi DB (spec §10 chỉ log Error cho
            // lỗi DB thật). Không nuốt im lặng: ghi Debug để còn dấu vết khi soi counter.
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Debug,
                Category = LogCategory.Request,
                Message = "Request đã hủy — bỏ qua ghi usage counter cho client key.",
                ClientKeyId = id,
            });
        }
        catch (Exception ex)
        {
            // Fail-open: counter là telemetry — DB lỗi không được phá request đã 2xx (spec §10)
            log.Error("Không ghi được usage counter cho client key.", ex, LogCategory.Request);
        }
    }
}
