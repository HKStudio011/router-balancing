using System.Security.Cryptography;
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
    private readonly IProxyEchoClient _echo;

    /// <inheritdoc/>
    public ProxyService(
        IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector, IProxyPool pool,
        IProxyEchoClient echo)
    {
        _db = db;
        _protector = protector;
        _pool = pool;
        _echo = echo;
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

    /// <inheritdoc/>
    public async Task<ProxyTestResult> TestAsync(long proxyId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var row = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == proxyId, ct)
            ?? throw new KeyNotFoundException($"Proxy {proxyId} not found."); // id không có thật → không persist

        try
        {
            // Build attempt (gồm Unprotect) PHẢI nằm trong try: DPAPI hỏng
            // (CryptographicException) phải rơi vào catch dưới để persist LastTest* —
            // để ngoài thì catch arm CryptographicException là dead code (spec §5.4).
            var attempt = new ProxyAttempt(
                row.Id,
                new Uri($"{row.Scheme}://{row.Host}:{row.Port}"),
                row.Username,
                row.PasswordEncrypted is null ? null : _protector.Unprotect(row.PasswordEncrypted),
                $"{row.Scheme}://{row.Host}:{row.Port}");

            var result = await _echo.EchoAsync(attempt, ct);
            row.LastTestAt = DateTimeOffset.UtcNow;
            row.LastTestSuccess = true;
            row.LastTestMessage = null;
            row.LastTestIp = result.Ip;
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // người dùng hủy thật → không persist, không nuốt
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or CryptographicException)
        {
            // Timeout (TaskCanceled), lỗi mạng, lỗi decrypt — persist để UI hiện lỗi
            row.LastTestAt = DateTimeOffset.UtcNow;
            row.LastTestSuccess = false;
            row.LastTestMessage = ex.Message;
            row.LastTestIp = null; // không để IP cũ của lần test thành công trước hiển thị kèm lỗi mới
            await db.SaveChangesAsync(ct);
            return new ProxyTestResult(false, ex.Message, null, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Validate proxy tồn tại — tập rỗng = gỡ toàn bộ gán (không validate).
    /// Proxy tắt vẫn là "đã gán" trong DB; pool tự lọc enabled nên request tự Direct (D-A2).
    /// Dùng chung DbContext của caller, không tạo context riêng — tránh leak (D-A5).
    /// </summary>
    private static async Task ValidateProxyIdsAsync(RouterBalancingDbContext db,
        IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        var wanted = ids.ToHashSet();
        var found = await db.OutboundProxies.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);
        var missing = wanted.Except(found).ToList();
        if (missing.Count > 0)
            throw new KeyNotFoundException($"Proxy {missing[0]} not found.");
    }

    /// <inheritdoc/>
    public async Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Include junction: cần hàng cũ để diff (không include thì EF không biết remove gì)
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");
        await ValidateProxyIdsAsync(db, proxyIds, ct);

        // Diff-update thay whole-set replacement (D-A1): hàng cũ không trong tập mới → Remove,
        // hàng mới → Add, hàng trùng → giữ nguyên. Re-save cùng tập = không thao tác → idempotent,
        // không đụng hàng đang track nên không ném InvalidOperationException trùng PK.
        var wanted = proxyIds.ToHashSet();
        foreach (var row in provider.ProviderProxies.Where(r => !wanted.Contains(r.ProxyId)).ToList())
            provider.ProviderProxies.Remove(row);
        var existing = provider.ProviderProxies.Select(r => r.ProxyId).ToHashSet();
        foreach (var id in wanted.Where(id => !existing.Contains(id)))
            provider.ProviderProxies.Add(new ProviderProxy { ProviderId = provider.Id, ProxyId = id });

        provider.ProxyMode = mode;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <inheritdoc/>
    public async Task AssignAccountProxiesAsync(long accountId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Include junction: cần hàng cũ để diff (không include thì EF không biết remove gì)
        var account = await db.ProviderAccounts
            .Include(a => a.AccountProxies)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new KeyNotFoundException($"Account {accountId} not found.");
        await ValidateProxyIdsAsync(db, proxyIds, ct);

        // Diff-update thay whole-set replacement (D-A1): hàng cũ không trong tập mới → Remove,
        // hàng mới → Add, hàng trùng → giữ nguyên. Re-save cùng tập = không thao tác → idempotent,
        // không đụng hàng đang track nên không ném InvalidOperationException trùng PK.
        var wanted = proxyIds.ToHashSet();
        foreach (var row in account.AccountProxies.Where(r => !wanted.Contains(r.ProxyId)).ToList())
            account.AccountProxies.Remove(row);
        var existing = account.AccountProxies.Select(r => r.ProxyId).ToHashSet();
        foreach (var id in wanted.Where(id => !existing.Contains(id)))
            account.AccountProxies.Add(new ProviderAccountProxy { AccountId = account.Id, ProxyId = id });

        account.ProxyMode = mode;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProxyAssignment>> GetAssignmentsAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .Include(p => p.Accounts)
                .ThenInclude(a => a.AccountProxies)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var result = new List<ProxyAssignment>
        {
            new()
            {
                Id = provider.Id, Name = provider.Name, IsProvider = true,
                Mode = provider.ProxyMode,
                ProxyIds = provider.ProviderProxies.Select(x => x.ProxyId).ToList(),
            },
        };
        foreach (var acc in provider.Accounts)
        {
            result.Add(new ProxyAssignment
            {
                Id = acc.Id, Name = acc.Name, IsProvider = false,
                Mode = acc.ProxyMode,
                ProxyIds = acc.AccountProxies.Select(x => x.ProxyId).ToList(),
            });
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>> GetReverseAssignmentsAsync(
        CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();

        // EF projection (không Include) — đúng 2 query tổng, không N+1 (D-A6)
        var providerRows = await db.Providers.AsNoTracking()
            .Where(p => p.ProviderProxies.Count > 0)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Mode = p.ProxyMode,
                ProxyIds = p.ProviderProxies.Select(x => x.ProxyId).ToList(),
            })
            .ToListAsync(ct);
        var accountRows = await db.ProviderAccounts.AsNoTracking()
            .Where(a => a.AccountProxies.Count > 0)
            .Select(a => new
            {
                a.Id,
                a.Name,
                Mode = a.ProxyMode,
                ProxyIds = a.AccountProxies.Select(x => x.ProxyId).ToList(),
            })
            .ToListAsync(ct);

        var map = new Dictionary<long, List<ProxyUsage>>();
        foreach (var p in providerRows)
            AddUsage(map, p.ProxyIds, new ProxyUsage(p.Id, p.Name, IsProvider: true, p.Mode));
        foreach (var a in accountRows)
            AddUsage(map, a.ProxyIds, new ProxyUsage(a.Id, a.Name, IsProvider: false, a.Mode));

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ProxyUsage>)kv.Value);
    }

    /// <summary>1 proxy có nhiều scope — cộng dồn usage thay vì ghi đè theo key proxy.</summary>
    private static void AddUsage(Dictionary<long, List<ProxyUsage>> map, List<long> proxyIds, ProxyUsage usage)
    {
        foreach (var proxyId in proxyIds)
        {
            if (!map.TryGetValue(proxyId, out var list))
                map[proxyId] = list = [];
            list.Add(usage);
        }
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
