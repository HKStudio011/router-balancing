using System.Text.RegularExpressions;

namespace RouterBalancing.Core.Settings;

/// <summary>
/// Validate SettingsDraft trước khi ghi setting — trả map tên field → key lỗi i18n.
/// Validator không chứa text hiển thị: UI dịch key sang ngôn ngữ hiện tại nên
/// thêm ngôn ngữ mới không phải đụng vào đây.
/// </summary>
public static class SettingsValidator
{
    /// <summary>Mã ngôn ngữ file JSON trong translations/ (vd "ja", "zh-Hans") — trùng pattern JsonTranslationLoader.</summary>
    private static readonly Regex LanguageCodePattern =
        new("^[a-z]{2}(-[A-Za-z]{2,4})?$", RegexOptions.Compiled);

    /// <summary>Kiểm tra toàn bộ rule của bảng Settings; trả rỗng khi hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(SettingsDraft draft)
    {
        var errors = new Dictionary<string, string>();

        // Chỉ validate format mã; ngôn ngữ không khả dụng (thiếu file) được
        // LocalizationService resolve fallback — không phải lỗi của người dùng.
        if (draft.Language is not ("auto" or "en" or "vi")
            && !LanguageCodePattern.IsMatch(draft.Language))
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

        // 0 hợp lệ (tắt watchdog) nên range bắt đầu tại 0
        if (draft.UpstreamIdleTimeoutSec is < 0 or > 3600)
            errors[nameof(SettingsDraft.UpstreamIdleTimeoutSec)] = "settings.error.upstreamIdleTimeout";

        // 0 hợp lệ (tắt cooldown quota) — cùng khoảng với watchdog
        if (draft.ProviderQuotaBlockSec is < 0 or > 3600)
            errors[nameof(SettingsDraft.ProviderQuotaBlockSec)] = "settings.error.quotaBlock";

        // Rỗng = không có đường dẫn để ghi (service có thể fallback mặc định, nhưng UI Save phải chặn
        // để user thấy ngay field sai thay vì âm thầm quay về mặc định).
        if (string.IsNullOrWhiteSpace(draft.OpenCodeConfigPath)
            || draft.OpenCodeConfigPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            errors[nameof(SettingsDraft.OpenCodeConfigPath)] = "settings.error.openCodeConfigPath";

        // DatasetExportPath khác OpenCodeConfigPath: rỗng là hợp lệ (writer tự suy ra mặc định
        // AppData), chỉ chặn độ dài vượt MAX_PATH Windows — writer sẽ fail khi ghi chứ không báo UI.
        if (draft.DatasetExportPath.Length > 260)
            errors[nameof(SettingsDraft.DatasetExportPath)] = "settings.error.datasetPath";

        // 0 hợp lệ (giữ mãi, không purge) — range dưới bắt đầu tại 0, range trên như logRetentionDays
        if (draft.DatasetRetentionDays is < 0 or > 3650)
            errors[nameof(SettingsDraft.DatasetRetentionDays)] = "settings.error.datasetRetention";

        return errors;
    }
}
