using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Settings;

/// <inheritdoc cref="IAppSettingsService"/>
public sealed class AppSettingsService : IAppSettingsService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly Dictionary<string, string> _cache = new();

    // Watchdog/ProxyHost đọc cache song song với thread gọi Set — Dictionary không
    // thread-safe, mọi truy cập _cache (kể cả nạp ở ctor) phải qua lock này.
    private readonly object _cacheLock = new();

    private bool _disposed;

    public event Action? SettingsChanged;

    public AppSettingsService(IDbContextFactory<RouterBalancingDbContext> db)
    {
        _db = db;
        using var context = _db.CreateDbContext();
        lock (_cacheLock)
        {
            foreach (var row in context.AppSettings.AsNoTracking())
            {
                _cache[row.Key] = row.ValueJson;
            }
        }
    }

    public string Language => Get(SettingsKeys.Language, "auto");

    public string Theme => Get(SettingsKeys.Theme, "system");

    public int Port => Get(SettingsKeys.Port, 8317);

    public bool LanAccess => Get(SettingsKeys.LanAccess, false);

    public bool CloseToTray => Get(SettingsKeys.CloseToTray, true);

    public bool StartWithWindows => Get(SettingsKeys.StartWithWindows, false);

    public int MaxRetry => Get(SettingsKeys.MaxRetry, 3);

    public int WatchdogIntervalSec => Get(SettingsKeys.WatchdogIntervalSec, 60);

    public int ProviderProbeTimeoutSec => Get(SettingsKeys.ProviderProbeTimeoutSec, 60);

    public int LogRetentionDays => Get(SettingsKeys.LogRetentionDays, 90);

    public int StatsErrorRateThreshold => Get(SettingsKeys.StatsErrorRateThreshold, 10);

    public T Get<T>(string key, T defaultValue = default!)
    {
        bool found;
        string? json;
        lock (_cacheLock)
        {
            found = _cache.TryGetValue(key, out json);
        }
        // Deserialize ngoài lock: string bất biến nên an toàn, tránh giữ lock khi parse
        return found ? JsonSerializer.Deserialize<T>(json!)! : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        lock (_cacheLock)
        {
            // Ghi đồng bộ: SQLite local rất nhanh, và lỗi phải nổi lên cho UI toast thay vì nuốt.
            // Persist trước, cache sau: nếu SaveChanges ném thì cache giữ nguyên giá trị cũ
            // → cache và DB không âm thầm lệch nhau, event cũng không phát.
            Persist(key, json);
            _cache[key] = json;
        }
        // Phát ngoài lock: subscriber chậm không giữ lock chặn thread đọc khác
        SettingsChanged?.Invoke();
    }

    private void Persist(string key, string json)
    {
        using var db = _db.CreateDbContext();
        var entity = db.AppSettings.Find(key);
        if (entity is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, ValueJson = json });
        }
        else
        {
            entity.ValueJson = json;
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
