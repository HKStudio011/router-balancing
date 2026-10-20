namespace RouterBalancing.Core.Tools;

/// <summary>
/// Lựa chọn tick đã nhớ của trang Tools (C-G) — id entity model/combo, không phải key config.
/// Lưu trong AppSettings dạng JSON; <see cref="ToolsSelectionResolver"/> lọc id đã bị xoá trước khi dùng.
/// </summary>
/// <param name="ModelIds">Id entity model đã tick.</param>
/// <param name="ComboIds">Id entity combo đã tick.</param>
public sealed record ToolsSelection(long[] ModelIds, long[] ComboIds);
