using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Usage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IRequestUsageSink"/>
public sealed class RequestUsageSink(
    IClientKeyService keys,
    IUsageDailyService usage,
    IClientKeyRateLimiter limiter,
    ILogService log) : IRequestUsageSink
{
    public async Task RecordAsync(long? clientKeyId, long? providerAccountId, int promptTokens,
        int completionTokens, CancellationToken ct = default)
    {
        var totalTokens = promptTokens + completionTokens;

        // TPM window là gate thật (in-memory) — cộng TRƯỚC, không phụ thuộc DB (spec §5/§10).
        // Không có usage thì totalTokens = 0, không ảnh hưởng cửa sổ.
        if (clientKeyId is long keyId && totalTokens > 0)
            limiter.AddTokens(keyId, totalTokens);

        if (clientKeyId is null && providerAccountId is null) return;

        try
        {
            if (clientKeyId is long keyIdForDb && totalTokens > 0)
                await keys.RecordTokensAsync(keyIdForDb, promptTokens, completionTokens, ct);

            // Account request được tính cho mọi 2xx, kể cả khi upstream thiếu usage;
            // token chỉ cộng phần parse được (0 nếu thiếu). Client key request đã được
            // middleware đếm lúc auth nên không ghi lại ở đây.
            if (providerAccountId is long accountId)
                await usage.RecordAsync(
                    UsageSubjectKind.ProviderAccount, accountId, requests: 1, tokens: totalTokens, ct);
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
                Message = "Request đã hủy — bỏ qua ghi usage counter.",
                ClientKeyId = clientKeyId,
            });
        }
        catch (Exception ex)
        {
            // Fail-open: usage là telemetry — DB lỗi không được phá request đã 2xx (spec §10)
            log.Error("Không ghi được usage counter cho client key/provider account.", ex,
                LogCategory.Request);
        }
    }
}
