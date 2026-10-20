namespace RouterBalancing.Core.Providers;

/// <summary>
/// Khả năng của model. Mọi field nullable: provider trả gì biết đó —
/// <see langword="null"/> = không rõ, service chỉ ghi fact khi nguồn phát biểu.
/// Danh sách modality/effort rỗng vẫn là "có phát biểu" (đã biết là không có mục nào).
/// </summary>
/// <param name="ContextWindow">Số token context tối đa; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsVision">Model nhận input hình ảnh; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsThink">Model suy nghĩ (reasoning) được; <see langword="null"/> = không rõ.</param>
/// <param name="ThinkEfforts">Các mức think effort đã biết, ví dụ <c>["low","high"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="InputModalities">Các modality đầu vào, ví dụ <c>["text","image"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="OutputModalities">Các modality đầu ra, ví dụ <c>["text"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsTools">
/// Model gọi được tools (function calling); <see langword="null"/> = nguồn không phát biểu.
/// Optional cuối record để nguồn cũ không phải truyền (R-T6-4).
/// </param>
public sealed record ModelMetadata(
    int? ContextWindow,
    bool? SupportsVision,
    bool? SupportsThink,
    IReadOnlyList<string>? ThinkEfforts,
    IReadOnlyList<string>? InputModalities,
    IReadOnlyList<string>? OutputModalities,
    bool? SupportsTools = null);
