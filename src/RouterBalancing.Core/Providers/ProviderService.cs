using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IProviderService"/>
public sealed class ProviderService : IProviderService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;

    public ProviderService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    public async Task<Provider?> GetAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default)
    {
        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Type = draft.Type,
            BaseUrl = draft.BaseUrl.Trim().TrimEnd('/'),
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

    public async Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        provider.Name = draft.Name.Trim();
        provider.Type = draft.Type;
        provider.BaseUrl = draft.BaseUrl.Trim().TrimEnd('/');
        provider.MaxConcurrent = draft.MaxConcurrent;
        // Key rỗng khi sửa = giữ nguyên key cũ — không bao giờ ghi đè bằng chuỗi rỗng
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.ApiKeyEncrypted = _protector.Protect(draft.ApiKey);
        }
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

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

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        provider.Enabled = enabled;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
