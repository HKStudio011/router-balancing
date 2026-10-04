using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Ping định kỳ <c>GET /v1/models</c> mọi provider enabled (spec manual-retry §3.5):
/// 401/403 → park <c>Unauthorized</c>, 404 → <c>NotFound</c>, mạng/timeout → <c>Unreachable</c>,
/// 429/5xx bỏ qua; 2xx + đang park + <c>pingParkedProviders</c> → unpark. Hosted service;
/// unit test gọi trực tiếp <see cref="PingAllAsync"/> (V7: delay-first, không fake timer).
/// </summary>
public sealed class ProviderPingService(
    IDbContextFactory<RouterBalancingDbContext> db,
    IHttpClientFactory http,
    ISecretProtector protector,
    IAppSettingsService settings,
    IManualRetryStore store,
    ILogService log) : BackgroundService
{
    /// <summary>
    /// Vòng đời hosted: delay TRƯỚC tick đầu (app vừa lên không ping ngay — tránh kẹp
    /// startup) rồi ping 1 lượt; interval đọc per-call — đổi setting có hiệu lực ngay.
    /// Host stop → thoát im lặng.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PingIntervalSec), stoppingToken);
                await PingAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — không phải lỗi
            }
        }
    }

    /// <summary>
    /// Ping 1 lượt mọi provider enabled — public để unit test gọi trực tiếp, không chờ tick.
    /// </summary>
    /// <param name="ct">Token hủy theo host stop.</param>
    public async Task PingAllAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var providers = await context.Providers.AsNoTracking()
            .Where(p => p.Enabled)
            .Include(p => p.Accounts)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        foreach (var provider in providers)
        {
            if (provider.Type == ProviderType.Anthropic)
                continue; // app chưa serve Anthropic — ping chỉ tạo noise (§3.5)

            var wasParked = store.IsProviderParked(provider.Id);
            if (wasParked && !settings.PingParkedProviders)
                continue; // mặc định không ping provider đang parked (§3.5)

            // TK enabled đầu tiên chưa park — tránh dùng key của TK đã bị park rồi oan
            // park provider; không còn TK hợp lệ → bỏ qua provider trong lượt này
            var account = provider.Accounts
                .FirstOrDefault(a => a.Enabled && !store.IsAccountParked(a.Id));
            if (account is null)
                continue;

            await PingOneAsync(provider, account, wasParked, ct);
        }
    }

    private async Task PingOneAsync(Provider provider, ProviderAccount account,
        bool wasParked, CancellationToken ct)
    {
        try
        {
            var key = account.ApiKeyEncrypted.Length > 0
                ? protector.Unprotect(account.ApiKeyEncrypted)
                : string.Empty; // TK no-key → không header auth (D7)

            // Ping đi đúng proxy của provider (giống TestConnectionAsync, D4)
            ProxyTarget.Current.Value = new ProxyTarget(provider, null);
            try
            {
                using var request = ProviderRequestFactory.Create(provider, key);
                using var response = await http
                    .CreateClient(ProviderRequestFactory.HttpClientName)
                    .SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    if (wasParked)
                    {
                        store.Unpark(ManualRetryLevel.Provider, provider.Id, "");
                        SafeLog(() => log.Info(
                            $"Provider '{provider.Name}' phục hồi qua ping — tự gỡ khỏi danh sách",
                            LogCategory.App));
                    }
                    return;
                }

                var code = (int)response.StatusCode;
                ManualRetryReason? reason = code switch
                {
                    401 or 403 => ManualRetryReason.Unauthorized,
                    404 => ManualRetryReason.NotFound,
                    _ => null,
                };
                if (reason is { } r)
                    ParkAndWarn(provider, wasParked, r, $"HTTP {code}");
                // 429/5xx/4xx khác — lỗi tạm thời, failover/request lo (§3.5): bỏ qua
            }
            finally
            {
                ProxyTarget.Current.Value = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop giữa ping — không phải lỗi
        }
        catch (HttpRequestException)
        {
            ParkAndWarn(provider, wasParked, ManualRetryReason.Unreachable, "lỗi mạng");
        }
        catch (TaskCanceledException)
        {
            // Timeout per-request của ProviderProbeTimeoutHandler (§3.8) → coi như unreachable
            ParkAndWarn(provider, wasParked, ManualRetryReason.Unreachable, "lỗi mạng");
        }
        catch (Exception ex)
        {
            // Bug/DPAPI hỏng/EF — KHÔNG park để tránh park oan; log Error best-effort (I2)
            SafeLog(() => log.Error(
                $"Lỗi ping provider '{provider.Name}': {ex.Message}", ex, LogCategory.App));
        }
    }

    private void ParkAndWarn(Provider provider, bool wasParked,
        ManualRetryReason reason, string reasonText)
    {
        try
        {
            // Store tự ghi Warn transition đúng 1 lần (đã SafeLog trong store)
            store.Park(ManualRetryLevel.Provider, provider.Id, "", reason);
        }
        catch
        {
            // Nuốt chủ đích: store lỗi không được phá vòng ping
        }

        // Chỉ log khi state đổi — đã parked thì store (cùng lý do) và ping cùng im lặng
        if (wasParked) return;
        SafeLog(() => log.Warn(
            $"Ping provider '{provider.Name}' thất bại ({reasonText}) — đưa vào danh sách retry thủ công",
            LogCategory.App));
    }

    /// <summary>Log ném (SQLite sập...) không được phá vòng ping — pattern SafeLog của store (I2).</summary>
    private static void SafeLog(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Nuốt chủ đích
        }
    }
}
