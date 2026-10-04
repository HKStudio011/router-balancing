namespace RouterBalancing.Core.Engine;

/// <summary>
/// Sinh key i18n cho badge cấp và nhãn lý do trong card manual retry Dashboard.
/// </summary>
/// <remarks>
/// Tại sao phải có helper thay vì ToLowerInvariant trực tiếp ở Razor: key trong
/// <c>Translations</c> là camelCase theo spec §3.6 (manualRetry.reason.modelNotFound),
/// từ điển phân biệt hoa thường — hạ nguyên cả từ (notFound → notfound) sẽ miss và
/// UI hiển thị raw key thay vì nhãn.
/// </remarks>
public static class ManualRetryI18n
{
    /// <summary>Key badge cấp — vd. <c>manualRetry.level.provider</c>.</summary>
    /// <param name="level">Cấp gây lỗi cần hiển thị.</param>
    public static string LevelKey(ManualRetryLevel level) =>
        $"manualRetry.level.{Camel(level.ToString())}";

    /// <summary>Key nhãn lý do — vd. <c>manualRetry.reason.modelNotFound</c>.</summary>
    /// <param name="reason">Lý do vào danh sách retry thủ công.</param>
    public static string ReasonKey(ManualRetryReason reason) =>
        $"manualRetry.reason.{Camel(reason.ToString())}";

    // Hạ CHỈ ký tự đầu (PascalCase → camelCase) đúng hình key spec §3.6 — hạ cả từ
    // sẽ phá key hai chữ (notFound/modelNotFound) vì từ điển i18n phân biệt hoa thường.
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
