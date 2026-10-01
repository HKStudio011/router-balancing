namespace RouterBalancing.Core.Settings;

/// <summary>Tên key trong bảng AppSettings — tập trung để không gõ sai.</summary>
public static class SettingsKeys
{
    /// <summary>"auto" | "en" | "vi" — auto theo ngôn ngữ hệ thống.</summary>
    public const string Language = "language";

    /// <summary>"light" | "dark" | "system".</summary>
    public const string Theme = "theme";

    public const string Port = "port";

    public const string CloseToTray = "closeToTray";

    public const string StartWithWindows = "startWithWindows";

    /// <summary>Dùng chung cho: số lần retry watchdog VÀ ngưỡng lỗi liên tiếp (spec Quyết định #11).</summary>
    public const string MaxRetry = "maxRetry";

    public const string WatchdogIntervalSec = "watchdogIntervalSec";

    public const string DefaultMaxConcurrent = "defaultMaxConcurrent";

    public const string LogRetentionDays = "logRetentionDays";

    public const string StatsErrorRateThreshold = "statsErrorRateThreshold";
}
