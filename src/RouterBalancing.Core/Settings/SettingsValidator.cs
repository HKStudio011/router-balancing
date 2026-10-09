namespace RouterBalancing.Core.Settings;

/// <summary>
/// Validate SettingsDraft trước khi ghi setting — trả map tên field → key lỗi i18n.
/// Validator không chứa text hiển thị: UI dịch key sang ngôn ngữ hiện tại nên
/// thêm ngôn ngữ mới không phải đụng vào đây.
/// </summary>
public static class SettingsValidator
{
    /// <summary>Kiểm tra toàn bộ rule của bảng Settings; trả rỗng khi hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(SettingsDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (draft.Language is not ("auto" or "en" or "vi"))
            errors[nameof(SettingsDraft.Language)] = "settings.error.language";

        if (draft.Theme is not ("light" or "dark" or "system"))
            errors[nameof(SettingsDraft.Theme)] = "settings.error.theme";

        if (draft.Port is < 1024 or > 65535)
            errors[nameof(SettingsDraft.Port)] = "settings.error.port";

        if (draft.ProviderProbeTimeoutSec is < 1 or > 600)
            errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)] = "settings.error.probeTimeout";

        if (draft.LogRetentionDays is < 1 or > 3650)
            errors[nameof(SettingsDraft.LogRetentionDays)] = "settings.error.retention";

        if (draft.StatsErrorRateThreshold is < 1 or > 100)
            errors[nameof(SettingsDraft.StatsErrorRateThreshold)] = "settings.error.threshold";

        // 0 hợp lệ (tắt retry) nên range bắt đầu tại 0, không phải 1 như các rule kia
        if (draft.TransientMaxRetries is < 0 or > 10)
            errors[nameof(SettingsDraft.TransientMaxRetries)] = "settings.error.transientRetries";

        if (draft.TransientBackoffBaseMs is < 250 or > 4000)
            errors[nameof(SettingsDraft.TransientBackoffBaseMs)] = "settings.error.transientBackoffBase";

        return errors;
    }
}
