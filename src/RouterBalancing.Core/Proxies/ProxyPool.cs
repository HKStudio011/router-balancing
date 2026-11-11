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

    /// <summary>
    /// Thời gian chặn exit sau IP-abuse 403 (spec opencode-free-gate-v2 §7.3) — constant 15m,
    /// CHƯA setting hóa (setting là backlog; khác <see cref="DownCooldown"/> ngắn hạn vì
    /// upstream phạt egress theo cửa sổ dài, thử lại sớm chỉ kéo dài penalty).
    /// </summary>
    public static readonly TimeSpan BlockedTtl = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogService _log;
    private readonly object _gate = new();

    // ===== State dưới lock — 1 list entries + down-state + block-state + cursor RR =====
    private List<ProxyAttempt> _entries = [];
    private Dictionary<long, DateTimeOffset> _downUntil = [];
    // Block do upstream từ chối egress (IP-abuse 403) — tách hẳn _downUntil vì lý do khác:
    // lỗi kết nối tự hồi phục 60s, còn egress bị phạt thì chỉ hết sau TTL (ReportSuccess
    // của handler cho mọi HTTP response không được phép xóa — spec §7.3).
    private Dictionary<long, DateTimeOffset> _blockedUntil = [];
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
    public ProxyAttempt? GetNext() => GetNext(null);

    /// <inheritdoc/>
    public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            PruneExpiredBlocks(now);
            for (var step = 0; step < _entries.Count; step++)
            {
                var index = (_cursor + step) % _entries.Count;
                if (IsDown(index, now))
                {
                    continue;
                }
                if (allowedIds is not null && !allowedIds.Contains(_entries[index].Id))
                {
                    continue;
                }

                _cursor = (index + 1) % _entries.Count;
                return _entries[index];
            }
            return null; // không còn proxy sống trong tập → direct
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            PruneExpiredBlocks(now);
            return _entries
                .Where(e => ids.Contains(e.Id))
                .Where(e => !IsDown(_entries.IndexOf(e), now))
                .OrderBy(e => e.Id)
                .ToList();
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
    public void ReportBlocked(long proxyId, TimeSpan ttl)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries.Any(e => e.Id == proxyId))
            {
                return; // proxy đã bị xóa/disable giữa chừng — không có gì để chặn
            }

            var now = _time.GetUtcNow();
            PruneExpiredBlocks(now);
            // Đang block = no-op (mirror ReportFailure): request song song cùng ăn 403 từ
            // exit này không gia hạn cooldown của nhau — hết TTL sớm nhất vẫn giữ nguyên.
            if (_blockedUntil.TryGetValue(proxyId, out var until) && until > now)
            {
                return;
            }

            _blockedUntil[proxyId] = now + ttl;
        }
    }

    /// <inheritdoc/>
    public void ReportSuccess(long proxyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            // Chỉ xóa down-state lỗi kết nối — block IP-abuse do dispatcher report KHÔNG được
            // xóa ở đây: handler credit path gọi ReportSuccess cho MỌI HTTP response (kể cả 403
            // vừa bị chặn), xóa block = mất cooldown, request sau lại đâm vào exit đã chết (§7.3).
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
            PruneExpiredBlocks(now);
            var result = new List<ProxyRuntimeStatus>(_entries.Count);
            foreach (var entry in _entries)
            {
                // Blocked hiện ra như "down" với downUntil = hết block: exit đang ngoài rotation
                // nên UI/budget retry của ProxyHealthHandler phải thấy đúng trạng thái đó.
                var until = UnavailableUntil(entry.Id, now);
                result.Add(new ProxyRuntimeStatus(entry.Id, entry.Endpoint, until is not null, until));
            }
            return result;
        }
    }

    /// <summary>
    /// Không còn khả dụng = down (lỗi kết nối) HAY đang block (IP-abuse) — dùng chung cho
    /// GetNext/GetLivingInOrder/Snapshot để mọi đường chọn exit đều bỏ qua exit đã bị chặn.
    /// </summary>
    private bool IsDown(int index, DateTimeOffset now) =>
        UnavailableUntil(_entries[index].Id, now) is not null;

    /// <summary>
    /// Thời điểm muộn nhất trong các cửa sổ còn hiệu lực của proxy (down/blocked);
    /// <see langword="null"/> = đang khả dụng.
    /// </summary>
    private DateTimeOffset? UnavailableUntil(long proxyId, DateTimeOffset now)
    {
        DateTimeOffset? until = null;
        if (_downUntil.TryGetValue(proxyId, out var downUntil) && downUntil > now)
            until = downUntil;
        // Lấy cửa sổ dài hơn khi cả hai cùng hiệu lực — exit chỉ khả dụng lại khi hết cả hai.
        if (_blockedUntil.TryGetValue(proxyId, out var blockedUntil) && blockedUntil > now
            && (until is null || blockedUntil > until))
            until = blockedUntil;
        return until;
    }

    /// <summary>
    /// Gỡ entry block đã hết TTL — gọi trong <see cref="_gate"/> ở mọi điểm truy cập state để
    /// dictionary không phình vì exit bị chặn rồi thôi (mirror <c>ProviderQuotaGate.PruneExpired</c>).
    /// </summary>
    private void PruneExpiredBlocks(DateTimeOffset now)
    {
        if (_blockedUntil.Count == 0)
            return;

        List<long>? expired = null;
        foreach (var (proxyId, until) in _blockedUntil)
        {
            if (until <= now)
                (expired ??= []).Add(proxyId);
        }
        if (expired is null)
            return;

        foreach (var proxyId in expired)
            _blockedUntil.Remove(proxyId);
    }

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
        // Block cũng giữ theo proxy còn tồn tại — CRUD proxy khác không mở lại exit đang bị phạt
        _blockedUntil = _blockedUntil.Where(kv => ids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _entries = loaded;
        _cursor = 0;
        _dirty = false;
    }
}
