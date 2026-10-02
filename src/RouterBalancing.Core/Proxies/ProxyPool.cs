using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Proxies;

/// <inheritdoc cref="IProxyPool"/>
public sealed class ProxyPool : IProxyPool
{
    /// <summary>Cooldown sau lỗi kết nối — passive recover, không probe riêng (spec §4.1).</summary>
    public static readonly TimeSpan DownCooldown = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogService _log;
    private readonly object _gate = new();

    // ===== State dưới lock — 1 list entries + down-state + cursor RR =====
    private List<ProxyAttempt> _entries = [];
    private Dictionary<long, DateTimeOffset> _downUntil = [];
    private int _cursor;
    private bool _dirty = true;

    /// <inheritdoc/>
    public ProxyPool(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector,
        TimeProvider time, ILogService log)
    {
        _db = db;
        _protector = protector;
        _time = time;
        _log = log;
    }

    /// <inheritdoc/>
    public ProxyAttempt? GetNext()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.Count == 0)
            {
                return null; // pool rỗng → direct
            }

            var now = _time.GetUtcNow();
            for (var step = 0; step < _entries.Count; step++)
            {
                var index = (_cursor + step) % _entries.Count;
                if (IsDown(index, now))
                {
                    continue;
                }

                _cursor = (index + 1) % _entries.Count;
                return _entries[index];
            }
            return null; // tất cả down → direct
        }
    }

    /// <inheritdoc/>
    public bool ReportFailure(long proxyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries.Any(e => e.Id == proxyId))
            {
                return false;
            }

            var now = _time.GetUtcNow();
            // Đã down = no-op: nhiều request fail song song không đụng cooldown của nhau,
            // không gia hạn, không trả true → handler không log lặp (spec §4.1)
            if (_downUntil.TryGetValue(proxyId, out var until) && until > now)
            {
                return false;
            }

            _downUntil[proxyId] = now + DownCooldown;
            return true;
        }
    }

    /// <inheritdoc/>
    public void ReportSuccess(long proxyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _downUntil.Remove(proxyId);
        }
    }

    /// <inheritdoc/>
    public void Invalidate()
    {
        // Chỉ set dirty — reload ở lần truy cập kế (dưới lock) để CRUD không block request
        // đang chạy; xem Design decision 4.
        lock (_gate)
        {
            _dirty = true;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProxyRuntimeStatus> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            var result = new List<ProxyRuntimeStatus>(_entries.Count);
            foreach (var entry in _entries)
            {
                var down = _downUntil.TryGetValue(entry.Id, out var until) && until > now;
                result.Add(new ProxyRuntimeStatus(entry.Id, entry.Endpoint, down, down ? until : null));
            }
            return result;
        }
    }

    private bool IsDown(int index, DateTimeOffset now) =>
        _downUntil.TryGetValue(_entries[index].Id, out var until) && until > now;

    private void EnsureLoaded()
    {
        if (_dirty)
        {
            Reload();
        }
    }

    /// <summary>
    /// Nạp lại list Enabled từ DB, decrypt password vào <see cref="ProxyAttempt"/>.
    /// Giữ down-state của proxy còn tồn tại (CRUD 1 proxy không reset cooldown proxy khác —
    /// Design decision 4); lỗi đọc DB → log Error + giữ list cũ (traffic vẫn chạy).
    /// </summary>
    private void Reload()
    {
        List<ProxyAttempt> loaded;
        try
        {
            using var db = _db.CreateDbContext();
            var rows = db.OutboundProxies.AsNoTracking()
                .Where(p => p.Enabled)
                .OrderBy(p => p.Id)
                .ToList();
            loaded = rows.Select(row => new ProxyAttempt(
                row.Id,
                // Uri không userinfo — .NET 10 không parse (dotnet/runtime#125341)
                new Uri($"{row.Scheme}://{row.Host}:{row.Port}"),
                row.Username,
                row.PasswordEncrypted is null ? null : _protector.Unprotect(row.PasswordEncrypted),
                $"{row.Scheme}://{row.Host}:{row.Port}")).ToList();
        }
        catch (Exception ex)
        {
            // Không nuốt: log + giữ list cũ để request vẫn đi được.
            _log.Error("Không nạp được danh sách proxy từ DB.", ex);
            // Load lần đầu thất bại (_entries rỗng): giữ dirty để lần GetNext/Snapshot kế
            // thử đọc DB lại — nếu clear luôn, pool trỏ direct vĩnh viễn chỉ vì 1 lỗi transient.
            // Đã có list cũ: clear dirty để không mỗi request đọc DB + spam log —
            // lần Invalidate kế (CRUD kế) sẽ thử đọc DB lại.
            _dirty = _entries.Count == 0;
            return;
        }

        var ids = loaded.Select(e => e.Id).ToHashSet();
        _downUntil = _downUntil.Where(kv => ids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _entries = loaded;
        _cursor = 0;
        _dirty = false;
    }
}
