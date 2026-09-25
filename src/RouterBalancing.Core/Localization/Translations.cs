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
        ["dashboard.status.label"] = "Status",
        ["dashboard.status.running"] = "Running",
        ["dashboard.status.stopped"] = "Stopped",
        ["dashboard.action.start"] = "Start",
        ["dashboard.action.stop"] = "Stop",
        ["dashboard.action.restart"] = "Restart",
        ["dashboard.msg.started"] = "Proxy started.",
        ["dashboard.msg.stopped"] = "Proxy stopped.",
        ["dashboard.msg.restarted"] = "Proxy restarted.",
        ["dashboard.msg.failed"] = "Action failed. See Logs for details.",
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
        ["dashboard.status.label"] = "Trạng thái",
        ["dashboard.status.running"] = "Đang chạy",
        ["dashboard.status.stopped"] = "Đã dừng",
        ["dashboard.action.start"] = "Khởi động",
        ["dashboard.action.stop"] = "Dừng",
        ["dashboard.action.restart"] = "Khởi động lại",
        ["dashboard.msg.started"] = "Đã khởi động proxy.",
        ["dashboard.msg.stopped"] = "Đã dừng proxy.",
        ["dashboard.msg.restarted"] = "Đã khởi động lại proxy.",
        ["dashboard.msg.failed"] = "Thao tác thất bại. Xem Nhật ký để biết chi tiết.",
    };

    /// <summary>Chọn bảng theo ngôn ngữ hiệu lực — không có bảng → English (fallback mọi keys).</summary>
    public static IReadOnlyDictionary<string, string> For(string language) =>
        language == "vi" ? Vietnamese : English;
}
