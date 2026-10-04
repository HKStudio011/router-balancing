namespace RouterBalancing.Core.Settings;

/// <summary>
/// Bản nháp toàn bộ form Settings — nạp từ <see cref="IAppSettingsService"/> khi mở trang,
/// validate rồi ghi lại theo từng nhóm. Property mutable để Blazor <c>@bind</c> nối trực tiếp
/// (record positional là init-only nên không bind được).
/// </summary>
public sealed record SettingsDraft
{
    public string Language { get; set; } = "auto";

    public string Theme { get; set; } = "system";

    public int Port { get; set; } = 8317;

    public bool LanAccess { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public int PingIntervalSec { get; set; } = 60;

    public bool PingParkedProviders { get; set; }

    public int ProviderProbeTimeoutSec { get; set; } = 60;

    public int LogRetentionDays { get; set; } = 90;

    public int StatsErrorRateThreshold { get; set; } = 10;
}
