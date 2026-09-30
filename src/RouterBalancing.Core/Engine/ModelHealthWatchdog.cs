using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Watchdog circuit 3C (spec §3.5): định kỳ probe model <c>ManualRetry</c> bằng chat 1 token —
/// 2xx đóng fuse, fail lùi lịch 60s×n floor <c>Retry-After</c>. Hosted service; unit test gọi
/// trực tiếp <see cref="ProbeDueAsync"/> (không fake timer). Đồng hồ inject qua <see cref="TimeProvider"/>.
/// </summary>
public sealed class ModelHealthWatchdog(
    IModelHealthStore health,
    IComboResolver resolver,
    IUpstreamClient upstream,
    ISecretProtector protector,
    IAppSettingsService settings,
    ILogService log,
    TimeProvider time) : BackgroundService
{
    /// <summary>
    /// Vòng đời hosted: delay thật <c>WatchdogIntervalSec</c> (đọc per-call — đổi setting
    /// có hiệu lực ngay) rồi quét 1 lượt. Host stop → thoát im lặng, không ghi probe fail.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.WatchdogIntervalSec), time, stoppingToken);
                await ProbeDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — không phải lỗi
            }
        }
    }

    /// <summary>
    /// Quét model <c>ManualRetry</c> có lịch probe ≤ <c>now</c> rồi probe từng model —
    /// public để unit test gọi trực tiếp, không chờ timer (spec §3.5).
    /// </summary>
    /// <param name="ct">Token hủy theo host stop.</param>
    public async Task ProbeDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var model in health.GetManualRetryModels())
        {
            if (model.NextProbeAt is not { } due || due > now)
                continue; // chưa tới lịch / hết lượt probe tự động (NextProbeAt = null)
            await ProbeOneAsync(model.ModelId, ct);
        }
    }

    private async Task ProbeOneAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var selection = await resolver.ResolveAsync(modelId, ct);
            if (selection is not SelectionSuccess success || success.Candidates.Count == 0)
            {
                // Model gỡ khỏi DB/combo while fuse mở — vẫn ghi probe fail để lùi lịch
                health.RecordProbeFailure(modelId);
                return;
            }

            var candidate = success.Candidates[0]; // provider enabled đầu tiên của model
            var key = ProviderKeyResolver.ResolveFirstEnabledKey(candidate.Provider, protector);
            if (key is null)
            {
                health.RecordProbeFailure(modelId);
                return;
            }

            // Dictionary thay vì record/anonymous: giữ key snake_case đúng contract OpenAI
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["model"] = candidate.Model.ModelId,
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = "ping" },
                },
                ["max_tokens"] = 1,
                ["stream"] = false,
            });

            using var response = await upstream.PostChatCompletionAsync(
                candidate.Provider, key, body, ct);
            if (response.IsSuccessStatusCode)
                health.RecordSuccess(modelId);
            else
                health.RecordProbeFailure(modelId,
                    RetryAfterParser.Parse(response.Headers.RetryAfter?.ToString(),
                        time.GetUtcNow()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop giữa probe — không tính là probe fail
        }
        catch (Exception ex)
        {
            // Mạng chết/lỗi khác khi probe là trạng thái bình thường. State TRƯỚC (entry là
            // memory, không throw), log SAU best-effort — log (I2) không được phá vòng watchdog
            health.RecordProbeFailure(modelId);
            try
            {
                log.Error($"Lỗi probe model '{modelId}': {ex.Message}", ex, LogCategory.App);
            }
            catch
            {
                // Nuốt chủ đích (pattern SafeLog của store): SQLite sập thì bỏ chi tiết log,
                // lịch probe vẫn đã lùi ở trên
            }
        }
    }
}
