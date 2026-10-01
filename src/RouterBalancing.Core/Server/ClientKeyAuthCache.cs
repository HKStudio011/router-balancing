using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <summary>Dòng match auth từ snapshot — record nhỏ cho hot path, không giữ entity EF.</summary>
public sealed record ClientKeyAuthInfo(long Id, string KeyHash, int? RatePerMinute, int? TokensPerMinute);

/// <summary>
/// Snapshot key đang enabled trong RAM: request không đụng DB, chỉ reload khi
/// <see cref="IClientKeyService.KeysChanged"/> — UI CRUD key có hiệu lực ngay, không restart proxy (spec §4.1).
/// </summary>
public sealed class ClientKeyAuthCache : IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IClientKeyService _keys;
    private readonly object _gate = new();
    private ClientKeyAuthInfo[]? _snapshot;
    private bool _dirty = true;
    private bool _disposed;

    public ClientKeyAuthCache(IDbContextFactory<RouterBalancingDbContext> db, IClientKeyService keys)
    {
        _db = db;
        _keys = keys;
        _keys.KeysChanged += OnKeysChanged;
    }

    public ClientKeyAuthInfo[] GetEnabled()
    {
        lock (_gate)
        {
            if (_snapshot is not null && !_dirty) return _snapshot;
            // Clear TRƯỚC khi đọc DB: KeysChanged bắn trong lúc đọc sẽ set lại _dirty,
            // để lock2 phân biệt "invalidate đến sau query" với trạng thái dirty cũ.
            _dirty = false;
        }

        // Đọc DB ngoài lock (không giữ lock khi I/O); nếu trong lúc đọc có KeysChanged nữa thì
        // _dirty vẫn true và snapshot cũ được giữ, lần gọi sau sẽ đọc lại — không bao giờ nuốt invalidate.
        ClientKeyAuthInfo[] fresh;
        try
        {
            using var db = _db.CreateDbContext();
            fresh = db.ClientKeys.AsNoTracking()
                .Where(k => k.Enabled)
                .OrderBy(k => k.Id)
                .Select(k => new ClientKeyAuthInfo(k.Id, k.KeyHash, k.RatePerMinute, k.TokensPerMinute))
                .ToArray();
        }
        catch
        {
            // Đọc lỗi: bật lại _dirty nếu không snapshot cũ (có thể thiếu invalidate) sẽ phục vụ
            // mãi tới CRUD kế tiếp — đúng lỗi "nuốt invalidate" mà fix này chặn.
            lock (_gate) { _dirty = true; }
            throw;
        }

        lock (_gate)
        {
            // _dirty=true → có KeysChanged trong lúc đọc → fresh thiếu thay đổi, không ghi đè;
            // _snapshot null (lần load đầu) vẫn nhận fresh để không trả null, _dirty giữ cho lần gọi sau.
            if (!_dirty || _snapshot is null)
                _snapshot = fresh;
            return _snapshot!;
        }
    }

    private void OnKeysChanged() => SetDirty();

    private void SetDirty()
    {
        lock (_gate) { _dirty = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Không unsubscribe = restart proxy nhiều lần giữ delegate vào cache đã chết (leak)
        _keys.KeysChanged -= OnKeysChanged;
    }
}
