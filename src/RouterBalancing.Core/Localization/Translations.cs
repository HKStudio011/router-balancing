namespace RouterBalancing.Core.Localization;

/// <summary>
/// Từ điển i18n phẳng: key dạng "nhóm.ten" → text. Thêm key mới tại đây
/// cho cả 2 ngôn ngữ — test LocalizationService_Fallback an toàn khi thiếu.
/// </summary>
public static class Translations
{
    /// <summary>Từ điển ngôn ngữ hệ thống → bảng ngôn ngữ nội bộ; thiếu → "en".</summary>
    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["app.title"] = "router-balancing",
        ["nav.dashboard"] = "Dashboard",
        ["nav.logs"] = "Logs",
        ["nav.settings"] = "Settings",
        ["panel.log.title"] = "Logs",
        ["panel.settings.title"] = "Settings",
        ["dashboard.title"] = "Dashboard",
    };

    public static readonly IReadOnlyDictionary<string, string> Vietnamese = new Dictionary<string, string>
    {
        ["app.title"] = "router-balancing",
        ["nav.dashboard"] = "Bảng điều khiển",
        ["nav.logs"] = "Nhật ký",
        ["nav.settings"] = "Cài đặt",
        ["panel.log.title"] = "Nhật ký",
        ["panel.settings.title"] = "Cài đặt",
        ["dashboard.title"] = "Bảng điều khiển",
    };

    /// <summary>Chọn bảng theo ngôn ngữ hiệu lực — không có bảng → English (fallback mọi keys).</summary>
    public static IReadOnlyDictionary<string, string> For(string language) =>
        language == "vi" ? Vietnamese : English;
}
