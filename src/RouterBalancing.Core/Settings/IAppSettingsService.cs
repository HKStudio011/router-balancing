namespace RouterBalancing.Core.Settings;

/// <summary>Setting đọc/ghi qua cache in-memory, đồng bộ xuống SQLite.</summary>
public interface IAppSettingsService
{
    /// <summary>Phát sau mỗi lần Set/SetApiKey — consumer re-read ngay.</summary>
    event Action? SettingsChanged;

    string Language { get; }

    string Theme { get; }

    /// <summary>Port ưa thích — ProxyHost dùng làm mốc tìm port trống.</summary>
    int Port { get; }

    bool ApiKeyEnabled { get; }

    bool CloseToTray { get; }

    bool StartWithWindows { get; }

    int MaxRetry { get; }

    int WatchdogIntervalSec { get; }

    int DefaultMaxConcurrent { get; }

    int LogRetentionDays { get; }

    int StatsErrorRateThreshold { get; }

    /// <summary>Đọc setting bất kỳ với mặc định khi chưa có trong DB.</summary>
    /// <exception cref="InvalidOperationException">Khi key là <see cref="SettingsKeys.ApiKey"/> — dùng <see cref="GetApiKey"/>.</exception>
    T Get<T>(string key, T defaultValue);

    /// <summary>Ghi cache + DB (đồng bộ) + phát <see cref="SettingsChanged"/>.</summary>
    /// <exception cref="InvalidOperationException">Khi key là <see cref="SettingsKeys.ApiKey"/> — dùng <see cref="SetApiKey"/>.</exception>
    void Set<T>(string key, T value);

    /// <summary>API key plaintext (đã giải mã); chuỗi rỗng nếu chưa đặt.</summary>
    string GetApiKey();

    /// <summary>Mã hóa DPAPI rồi lưu API key.</summary>
    void SetApiKey(string plain);
}
