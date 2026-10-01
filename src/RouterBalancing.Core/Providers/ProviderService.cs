using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IProviderService"/>
public sealed class ProviderService : IProviderService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    /// <inheritdoc/>
    public ProviderService(
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
    public async Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Provider?> GetAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var identifier = draft.Identifier.Trim();
        await EnsureIdentifierUsableAsync(db, identifier, excludeId: null, ct);

        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Identifier = identifier,
            Type = draft.Type,
            BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl),
            MaxConcurrent = draft.MaxConcurrent,
        };

        // Key ở create = tạo kèm account "Default" — key sống hoàn toàn ở ProviderAccount (spec §4.2)
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "Default",
                ApiKeyEncrypted = _protector.Protect(draft.ApiKey),
                Enabled = true,
                Weight = 100,
                Priority = 0,
            });
        }

        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        var identifier = draft.Identifier.Trim();
        await EnsureIdentifierUsableAsync(db, identifier, excludeId: id, ct);

        provider.Name = draft.Name.Trim();
        provider.Identifier = identifier;
        provider.Type = draft.Type;
        provider.BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl);
        provider.MaxConcurrent = draft.MaxConcurrent;
        // draft.ApiKey bị BỎ QUA khi update — key quản lý ở ProviderAccount (spec §4.2)
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        // Preset free không được xoá (D6) — ẩn nút ở UI chưa đủ, service tự chặn (spec §8.3)
        if (provider.IsPreset)
        {
            throw new InvalidOperationException($"Preset provider {id} cannot be deleted.");
        }

        // FK Models→Providers là ON DELETE CASCADE (xem RouterBalancingDbContext.OnModelCreating)
        // — xóa provider, DB tự xóa models con.
        db.Providers.Remove(provider);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        provider.Enabled = enabled;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ProviderTestResult> TestConnectionAsync(
        Provider provider, string? apiKeyOverride, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        ProviderTestResult result;
        try
        {
            // Override (key đang gõ trên form) ưu tiên; không có → account enabled đầu tiên.
            // Unprotect PHẢI nằm trong try: key DPAPI hỏng (CryptographicException) rơi vào
            // catch → fail với lý do, không ném ra UI.
            var key = apiKeyOverride;
            if (string.IsNullOrEmpty(key))
            {
                key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector);
            }

            using var request = ProviderRequestFactory.Create(provider, key ?? string.Empty);
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);

            result = response.IsSuccessStatusCode
                ? new ProviderTestResult(true, null, at)
                : new ProviderTestResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", at);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Timeout/Socket error/URL sai/key DPAPI hỏng → fail với lý do, không ném ra UI
            result = new ProviderTestResult(false, ex.Message, at);
            _log.Warn($"Test connection failed: {ex.Message}");
        }

        // Id == 0 = bản nháp chưa lưu — không có hàng để ghi LastTest*
        if (provider.Id != 0)
        {
            await PersistTestResultAsync(provider.Id, result, ct);
        }

        return result;
    }

    private async Task PersistTestResultAsync(long id, ProviderTestResult result, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (provider is null) return;
        provider.LastTestSuccess = result.Success;
        provider.LastTestAt = result.At;
        provider.LastTestMessage = result.Message;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Unique + chống segment-trùng (cần DB nên tách khỏi ProviderValidator) —
    /// ném ProviderValidationException mang dict key i18n.
    /// </summary>
    private static async Task EnsureIdentifierUsableAsync(
        RouterBalancingDbContext db, string identifier, long? excludeId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var duplicate = await db.Providers.AsNoTracking()
            .AnyAsync(p => p.Identifier == identifier
                && (excludeId == null || p.Id != excludeId), ct);
        if (duplicate)
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierDuplicate";
        }
        else
        {
            // Identifier = segment đầu của model id hiện có (vd model "openai/gpt-4o" của OpenRouter)
            // → client gửi chuỗi đó sẽ bị pin nhầm — so sánh ordinal để không over-reject với SQLite LIKE
            var prefix = identifier + "/";
            var modelIds = await db.Models.AsNoTracking()
                .Where(m => m.ModelId.StartsWith(prefix))
                .Select(m => m.ModelId)
                .ToListAsync(ct);
            if (modelIds.Any(m => m.StartsWith(prefix, StringComparison.Ordinal)))
            {
                errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierSegmentCollision";
            }
        }

        if (errors.Count > 0)
        {
            throw new ProviderValidationException(errors);
        }
    }
}
