namespace RouterBalancing.Core.Providers;

/// <summary>
/// Khả năng của model. Mọi field nullable: provider trả gì biết đó —
/// <see langword="null"/> = không rõ, service giữ nguyên giá trị hiện tại.
/// </summary>
/// <param name="ContextWindow">Số token context tối đa; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsVision">Model nhận input hình ảnh; <see langword="null"/> = không rõ.</param>
/// <param name="SupportsThink">Model suy nghĩ (reasoning) được; <see langword="null"/> = không rõ.</param>
/// <param name="ThinkEfforts">JSON array mức think effort, ví dụ <c>["low","high"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="InputModalities">JSON array modalities đầu vào, ví dụ <c>["text","image"]</c>; <see langword="null"/> = không rõ.</param>
/// <param name="OutputModalities">JSON array modalities đầu ra, ví dụ <c>["text"]</c>; <see langword="null"/> = không rõ.</param>
public sealed record ModelMetadata(
    int? ContextWindow,
    bool? SupportsVision,
    bool? SupportsThink,
    string? ThinkEfforts,
    string? InputModalities,
    string? OutputModalities);
