using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Proxies;

/// <inheritdoc cref="IProxyService"/>
public sealed class ProxyService : IProxyService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IProxyPool _pool;

    /// <inheritdoc/>
    public ProxyService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector, IProxyPool pool)
    {
        _db = db;
        _protector = protector;
        _pool = pool;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundProxy>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.OutboundProxies.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<OutboundProxy> CreateAsync(ProxyDraft draft, CancellationToken ct = default)
    {
        var (scheme, host, port, username) = Validate(draft);
        using var db = _db.CreateDbContext();
        await EnsureEndpointUsableAsync(db, scheme, host, port, excludeId: null, ct);

        var proxy = new OutboundProxy
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Username = username,
            // Invariant Design decision 5: username trống → không lưu password
            PasswordEncrypted = username is null || draft.Password is null
                ? null
                : _protector.Protect(draft.Password),
        };

        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
        return proxy;
    }

    /// <inheritdoc/>
    public async Task<OutboundProxy> UpdateAsync(long id, ProxyDraft draft, CancellationToken ct = default)
    {
        var (scheme, host, port, username) = Validate(draft);
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");
        await EnsureEndpointUsableAsync(db, scheme, host, port, excludeId: id, ct);

        proxy.Scheme = scheme;
        proxy.Host = host;
        proxy.Port = port;
        if (username is null)
        {
            // Username trống ⇒ xóa toàn bộ auth — không để password mồ côi (spec §3.2)
            proxy.Username = null;
            proxy.PasswordEncrypted = null;
        }
        else
        {
            proxy.Username = username;
            // Password null ⇒ giữ password đã encrypt (spec §3.2)
            if (draft.Password is not null)
            {
                proxy.PasswordEncrypted = _protector.Protect(draft.Password);
            }
        }

        proxy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
        return proxy;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");

        db.OutboundProxies.Remove(proxy);
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");

        proxy.Enabled = enabled;
        proxy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <summary>
    /// Validate draft thuần (không DB) + chuẩn hóa (scheme lowercase, trim host/username) —
    /// ném ProxyValidationException với dict key i18n.
    /// </summary>
    private static (string Scheme, string Host, int Port, string? Username) Validate(ProxyDraft draft)
    {
        var errors = ProxyValidator.Validate(draft);
        if (errors.Count > 0)
        {
            throw new ProxyValidationException(errors);
        }

        var username = string.IsNullOrWhiteSpace(draft.Username) ? null : draft.Username.Trim();
        return (draft.Scheme.Trim().ToLowerInvariant(), draft.Host.Trim(), draft.Port, username);
    }

    /// <summary>
    /// Unique endpoint theo <c>scheme://host:port</c>, host case-insensitive (Design decision 6) —
    /// cần DB nên tách khỏi ProxyValidator; ném ProxyValidationException gắn lỗi vào field Host.
    /// </summary>
    private static async Task EnsureEndpointUsableAsync(
        RouterBalancingDbContext db, string scheme, string host, int port, long? excludeId, CancellationToken ct)
    {
        // EF dịch ToLower() → SQLite lower() — so sánh được server-side
        var hostLower = host.ToLowerInvariant();
        var duplicate = await db.OutboundProxies.AsNoTracking()
            .AnyAsync(p => p.Scheme == scheme && p.Port == port && p.Host.ToLower() == hostLower
                && (excludeId == null || p.Id != excludeId), ct);
        if (duplicate)
        {
            throw new ProxyValidationException(new Dictionary<string, string>
            {
                [nameof(ProxyDraft.Host)] = "proxies.error.duplicate",
            });
        }
    }
}
