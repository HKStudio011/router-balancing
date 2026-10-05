namespace RouterBalancing.Core.Settings;

/// <summary>Tên key trong bảng AppSettings — tập trung để không gõ sai.</summary>
public static class SettingsKeys
{
    /// <summary>"auto" | "en" | "vi" — auto theo ngôn ngữ hệ thống.</summary>
    public const string Language = "language";

    /// <summary>"light" | "dark" | "system".</summary>
    public const string Theme = "theme";

    public const string Port = "port";

    /// <summary>Bật bind Kestrel mọi interface (LAN) — mặc định false, chỉ loopback (D-C1).</summary>
    public const string LanAccess = "lanAccess";

    public const string CloseToTray = "closeToTray";

    public const string StartWithWindows = "startWithWindows";

    /// <summary>Timeout per-request của client provider-probe (spec manual-retry §3.8) — 1..600 giây.</summary>
    public const string ProviderProbeTimeoutSec = "providerProbeTimeoutSec";

    public const string LogRetentionDays = "logRetentionDays";

    public const string StatsErrorRateThreshold = "statsErrorRateThreshold";
}
