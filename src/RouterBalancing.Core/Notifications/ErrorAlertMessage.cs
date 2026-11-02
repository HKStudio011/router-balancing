namespace RouterBalancing.Core.Notifications;

/// <summary>
/// Dựng nội dung thông báo lỗi từ bản đồ i18n + text lỗi thô. Pure function —
/// không đọc settings hay service nào.
/// </summary>
public static class ErrorAlertMessage
{
    /// <summary>Giới hạn độ dài phần text lỗi trong thông báo (ký tự UTF-16).</summary>
    public const int MaxMessageChars = 200;

    private const string Ellipsis = "…";

    /// <summary>
    /// Dựng message cho một cụm lỗi. 1 lỗi → dùng nguyên text lỗi (không bọc
    /// template cluster); nhiều lỗi → template <c>notify.error.cluster</c>
    /// với số lỗi, số giây của cửa sổ gom và message cuối.
    /// </summary>
    /// <param name="t">Hàm dịch i18n (call site truyền <c>key =&gt; L[key]</c>) — dạng Func để test không phải fake settings.</param>
    /// <param name="count">Số lỗi trong cụm.</param>
    /// <param name="window">Độ dài cửa sổ gom nhóm lỗi.</param>
    /// <param name="lastMessage">Message của lỗi gần nhất (chi tiết <c>LogEntry.Details</c> không bao giờ đi qua đây).</param>
    /// <returns>Nội dung hiển thị, phần text lỗi đã cắt theo <see cref="MaxMessageChars"/>.</returns>
    public static string Build(Func<string, string> t, int count, TimeSpan window, string lastMessage)
    {
        ArgumentNullException.ThrowIfNull(t);
        var safeMessage = Truncate(lastMessage);
        if (count == 1)
        {
            return safeMessage;
        }

        // Window là TimeSpan do cluster planner dựng (giây chẵn); TotalSeconds
        // an toàn cho notification — không cần format culture ở đây.
        return string.Format(t("notify.error.cluster"), count, (int)window.TotalSeconds, safeMessage);
    }

    /// <summary>
    /// Cắt chuỗi quá dài xuống <see cref="MaxMessageChars"/> ký tự và thêm marker
    /// "…". Nếu ký tự cuối của bản cắt là high surrogate thì bỏ luôn để không
    /// lại surrogate cụt (emoji bị cắt giữa cặp).
    /// </summary>
    public static string Truncate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= MaxMessageChars)
        {
            return value;
        }

        var cut = MaxMessageChars;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), Ellipsis);
    }
}
