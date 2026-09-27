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
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Provider?> GetAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default)
    {
        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Type = draft.Type,
            BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl),
            ApiKeyEncrypted = string.IsNullOrEmpty(draft.ApiKey)
                ? string.Empty
                : _protector.Protect(draft.ApiKey),
            MaxConcurrent = draft.MaxConcurrent,
        };

        using var db = _db.CreateDbContext();
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

        provider.Name = draft.Name.Trim();
        provider.Type = draft.Type;
        provider.BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl);
        provider.MaxConcurrent = draft.MaxConcurrent;
        // Key rỗng khi sửa = giữ nguyên key cũ — không bao giờ ghi đè bằng chuỗi rỗng
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.ApiKeyEncrypted = _protector.Protect(draft.ApiKey);
        }
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

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
            // Override (key đang gõ trên form) ưu tiên; không có → giải mã key đã lưu.
            // Decrypt PHẢI nằm trong try: key DPAPI hỏng (CryptographicException) rơi vào
            // catch → fail với lý do, không ném ra UI (sửa theo review Task 3 — nếu để
            // ngoài try thì CryptographicException trong catch filter là dead code).
            var key = apiKeyOverride;
            if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(provider.ApiKeyEncrypted))
            {
                key = _protector.Unprotect(provider.ApiKeyEncrypted);
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
}
