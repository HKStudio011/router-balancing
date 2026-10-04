using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IProviderAccountService"/>
public sealed class ProviderAccountService : IProviderAccountService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    /// <inheritdoc/>
    public ProviderAccountService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.ProviderAccounts
            .Where(a => a.ProviderId == providerId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default)
    {
        ProviderAccountValidator.ValidateAndThrow(draft, requireApiKey: true);

        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == draft.ProviderId, ct))
        {
            throw new KeyNotFoundException($"Provider {draft.ProviderId} not found.");
        }

        var name = draft.Name.Trim();
        if (await db.ProviderAccounts.AnyAsync(a => a.ProviderId == draft.ProviderId && a.Name == name, ct))
        {
            throw new InvalidOperationException($"Account name '{name}' already exists for provider {draft.ProviderId}.");
        }

        var account = new ProviderAccount
        {
            ProviderId = draft.ProviderId,
            Name = name,
            // NoKey → cột rỗng (không Protect("")); có key → DPAPI như cũ
            ApiKeyEncrypted = draft.NoKey ? string.Empty : _protector.Protect(draft.ApiKey.Trim()),
            Enabled = draft.Enabled,
            ModelPatterns = SerializePatterns(draft.ModelPatterns),
            Weight = draft.Weight,
            DailyTokenLimit = draft.DailyTokenLimit,
            DailyRequestLimit = draft.DailyRequestLimit,
        };

        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <inheritdoc/>
    public async Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default)
    {
        ProviderAccountValidator.ValidateAndThrow(draft, requireApiKey: false);

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");

        var name = draft.Name.Trim();
        if (await db.ProviderAccounts.AnyAsync(
                a => a.ProviderId == account.ProviderId && a.Id != id && a.Name == name, ct))
        {
            throw new InvalidOperationException($"Account name '{name}' already exists for provider {account.ProviderId}.");
        }

        account.Name = name;
        // Key rỗng (kể cả toàn khoảng trắng) khi sửa = giữ nguyên key cũ —
        // nếu không, "   ".Trim() sẽ persist key rỗng và traffic 401 âm thầm.
        // NoKey=true = chủ động XÓA key → cột rỗng (spec free-account D5)
        if (draft.NoKey)
        {
            account.ApiKeyEncrypted = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(draft.ApiKey))
        {
            account.ApiKeyEncrypted = _protector.Protect(draft.ApiKey.Trim());
        }
        account.Enabled = draft.Enabled;
        account.ModelPatterns = SerializePatterns(draft.ModelPatterns);
        account.Weight = draft.Weight;
        account.DailyTokenLimit = draft.DailyTokenLimit;
        account.DailyRequestLimit = draft.DailyRequestLimit;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");

        var remaining = await db.ProviderAccounts
            .CountAsync(a => a.ProviderId == account.ProviderId && a.Id != id, ct);
        if (remaining == 0)
        {
            // Provider không được trơ trọi không có key — UI chặn trước bằng nút disable
            throw new InvalidOperationException($"Account {id} is the last account of provider {account.ProviderId}.");
        }

        db.ProviderAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");
        account.Enabled = enabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // TestAllAsync set ProxyTarget(provider, account) — resolver đọc junction từ entity
        // đã load (spec §6), thiếu Include thì account test luôn đi Direct dù đã gán proxy.
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .Include(p => p.Accounts)
                .ThenInclude(a => a.AccountProxies)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var results = new List<ProviderAccountTestResult>();
        var enabled = provider.Accounts
            .Where(a => a.Enabled)
            .OrderBy(a => a.Id)
            .ToList();

        foreach (var account in enabled)
        {
            var at = DateTimeOffset.UtcNow;
            bool success;
            string? message;

            ProxyTarget.Current.Value = new ProxyTarget(provider, account);
            try
            {
                // No-key account (cột rỗng) → probe không header auth; Unprotect("") sẽ ném
                var key = string.IsNullOrEmpty(account.ApiKeyEncrypted)
                    ? string.Empty
                    : _protector.Unprotect(account.ApiKeyEncrypted);
                using var request = ProviderRequestFactory.Create(provider, key);
                using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                    .SendAsync(request, ct);
                success = response.IsSuccessStatusCode;
                message = success
                    ? null
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           or InvalidOperationException or CryptographicException)
            {
                // Timeout/Socket error/URL sai/key DPAPI hỏng → fail với lý do, không ném ra UI
                success = false;
                message = ex.Message;
                _log.Warn($"Account test failed ({account.Name}): {ex.Message}");
            }
            finally
            {
                ProxyTarget.Current.Value = null;
            }

            account.LastTestSuccess = success;
            account.LastTestAt = at;
            account.LastTestMessage = message;
            results.Add(new ProviderAccountTestResult(account.Id, account.Name, success, message));
        }

        // Provider badge = AND các account enabled; không có account enabled → trạng thái "chưa test"
        provider.LastTestSuccess = enabled.Count == 0
            ? null
            : enabled.All(a => a.LastTestSuccess == true);
        provider.LastTestAt = enabled.Count == 0 ? null : DateTimeOffset.UtcNow;
        provider.LastTestMessage = enabled.Count == 0
            ? null
            : string.Join("; ", enabled
                .Where(a => a.LastTestSuccess != true)
                .Select(a => $"{a.Name}: {a.LastTestMessage}"));

        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Rỗng → null (không cần field); có pattern → JSON array ["a","b"].</summary>
    private static string? SerializePatterns(string[] patterns) =>
        patterns.Length == 0 ? null : JsonSerializer.Serialize(patterns);
}
