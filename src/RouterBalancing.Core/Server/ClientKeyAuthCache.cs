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
        }

        // Đọc DB ngoài lock (không giữ lock khi I/O); nếu trong lúc đọc có KeysChanged nữa thì
        // _dirty vẫn true và snapshot cũ được giữ, lần gọi sau sẽ đọc lại — không bao giờ nuốt invalidate.
        using var db = _db.CreateDbContext();
        var fresh = db.ClientKeys.AsNoTracking()
            .Where(k => k.Enabled)
            .OrderBy(k => k.Id)
            .Select(k => new ClientKeyAuthInfo(k.Id, k.KeyHash, k.RatePerMinute, k.TokensPerMinute))
            .ToArray();

        lock (_gate)
        {
            if (_dirty)
            {
                _snapshot = fresh;
                _dirty = false;
            }
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
