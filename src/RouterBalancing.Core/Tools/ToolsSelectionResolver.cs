namespace RouterBalancing.Core.Tools;

/// <summary>
/// Quyết định lựa chọn tick khi mở trang Tools (spec §9.1): có bản ghi nhớ → tôn trọng
/// (kể cả rỗng), chưa có → mặc định C7; luôn bỏ id không còn tồn tại trong DB.
/// </summary>
public static class ToolsSelectionResolver
{
    /// <summary>
    /// Trả lựa chọn đã lọc theo id còn tồn tại. <paramref name="stored"/> null = chưa từng lưu
    /// (dùng <paramref name="defaultModelIds"/>, combo rỗng); khác null = dùng bản đã lưu, rỗng vẫn rỗng.
    /// </summary>
    /// <param name="stored">Bản ghi trong AppSettings; null = chưa có.</param>
    /// <param name="availableModelIds">Id model đang có trên trang (sau khi lọc model Enabled).</param>
    /// <param name="availableComboIds">Id combo đang có trong DB.</param>
    /// <param name="defaultModelIds">Tập mặc định C7 đã tính bởi service.</param>
    public static ToolsSelection Resolve(
        ToolsSelection? stored,
        IReadOnlyCollection<long> availableModelIds,
        IReadOnlyCollection<long> availableComboIds,
        IReadOnlyList<long> defaultModelIds)
    {
        ArgumentNullException.ThrowIfNull(availableModelIds);
        ArgumentNullException.ThrowIfNull(availableComboIds);
        ArgumentNullException.ThrowIfNull(defaultModelIds);

        if (stored is null)
        {
            return new ToolsSelection(
                defaultModelIds.Where(availableModelIds.Contains).ToArray(),
                []);
        }

        return new ToolsSelection(
            stored.ModelIds.Where(availableModelIds.Contains).ToArray(),
            stored.ComboIds.Where(availableComboIds.Contains).ToArray());
    }
}
