using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Settings;

/// <inheritdoc cref="IAppSettingsService"/>
public sealed class AppSettingsService : IAppSettingsService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly Dictionary<string, string> _cache = new();
    private bool _disposed;

    public event Action? SettingsChanged;

    public AppSettingsService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
        using var context = _db.CreateDbContext();
        foreach (var row in context.AppSettings.AsNoTracking())
        {
            _cache[row.Key] = row.ValueJson;
        }
    }

    public string Language => Get(SettingsKeys.Language, "auto");

    public string Theme => Get(SettingsKeys.Theme, "system");

    public int Port => Get(SettingsKeys.Port, 8317);

    public bool ApiKeyEnabled => Get(SettingsKeys.ApiKeyEnabled, false);

    public bool CloseToTray => Get(SettingsKeys.CloseToTray, true);

    public bool StartWithWindows => Get(SettingsKeys.StartWithWindows, false);

    public int MaxRetry => Get(SettingsKeys.MaxRetry, 3);

    public int WatchdogIntervalSec => Get(SettingsKeys.WatchdogIntervalSec, 60);

    public int DefaultMaxConcurrent => Get(SettingsKeys.DefaultMaxConcurrent, 4);

    public int LogRetentionDays => Get(SettingsKeys.LogRetentionDays, 90);

    public int StatsErrorRateThreshold => Get(SettingsKeys.StatsErrorRateThreshold, 10);

    public T Get<T>(string key, T defaultValue = default!)
    {
        GuardApiKey(key);
        return _cache.TryGetValue(key, out var json)
            ? JsonSerializer.Deserialize<T>(json)!
            : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        GuardApiKey(key);
        var json = JsonSerializer.Serialize(value);
        _cache[key] = json;
        // Ghi đồng bộ: SQLite local rất nhanh, và lỗi phải nổi lên cho UI toast thay vì nuốt
        Persist(key, json);
        SettingsChanged?.Invoke();
    }

    public string GetApiKey()
    {
        var stored = _cache.TryGetValue(SettingsKeys.ApiKey, out var json) ? json : null;
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        return _protector.Unprotect(stored);
    }

    public void SetApiKey(string plain)
    {
        var value = plain.Length == 0 ? string.Empty : _protector.Protect(plain);
        _cache[SettingsKeys.ApiKey] = value;
        Persist(SettingsKeys.ApiKey, value);
        SettingsChanged?.Invoke();
    }

    private static void GuardApiKey(string key)
    {
        if (key == SettingsKeys.ApiKey)
            throw new InvalidOperationException(
                "Không đọc/ghi API key qua Get/Set — dùng GetApiKey/SetApiKey để tránh để lộ plaintext.");
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
