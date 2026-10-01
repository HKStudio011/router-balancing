namespace RouterBalancing.Core.Settings;

/// <summary>Setting đọc/ghi qua cache in-memory, đồng bộ xuống SQLite.</summary>
public interface IAppSettingsService
{
    /// <summary>Phát sau mỗi lần Set — consumer re-read ngay.</summary>
    event Action? SettingsChanged;

    string Language { get; }

    string Theme { get; }

    /// <summary>Port ưa thích — ProxyHost dùng làm mốc tìm port trống.</summary>
    int Port { get; }

    bool CloseToTray { get; }

    bool StartWithWindows { get; }

    int MaxRetry { get; }

    int WatchdogIntervalSec { get; }

    int DefaultMaxConcurrent { get; }

    int LogRetentionDays { get; }

    int StatsErrorRateThreshold { get; }

    /// <summary>Đọc setting bất kỳ với mặc định khi chưa có trong DB.</summary>
    T Get<T>(string key, T defaultValue);

    /// <summary>Ghi cache + DB (đồng bộ) + phát <see cref="SettingsChanged"/>.</summary>
    void Set<T>(string key, T value);
}
