namespace RouterBalancing.Core.Tools;

/// <summary>
/// Một model trong bảng chọn của trang Tools — dữ liệu đủ để render row, không mang entity EF.
/// Capability đến từ <c>ModelCapabilityView</c> (Task 4): ba trạng thái —
/// <see langword="null"/> = chưa biết (không có fact), <see langword="false"/> = đã biết là không.
/// </summary>
/// <param name="Id">Id entity — value của checkbox chọn model.</param>
/// <param name="ModelId">Model id phía provider, cũng là key trong config opencode.</param>
/// <param name="DisplayName">Tên hiển thị; <see langword="null"/> → UI/generator fallback về <paramref name="ModelId"/>.</param>
/// <param name="ContextWindow">Context window (token); <see langword="null"/> = không rõ.</param>
/// <param name="Tools">Model gọi tool được (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="Reasoning">Model suy nghĩ được (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="Vision">Model nhận ảnh (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="AudioInput">Model nhận audio đầu vào (badge media — Task 10); <see langword="null"/> = chưa biết.</param>
/// <param name="VideoInput">Model nhận video đầu vào (badge media — Task 10); <see langword="null"/> = chưa biết.</param>
/// <param name="InputModalities">Các modality input khẳng định thuận (chỉ true, thứ tự ModalityKind
/// tăng dần); rỗng = không có phát biểu thuận nào.</param>
/// <param name="OutputModalities">Các modality output khẳng định thuận; rỗng = không có phát biểu thuận.</param>
public sealed record ToolsModelRow(
    long Id, string ModelId, string? DisplayName, int? ContextWindow,
    bool? Tools, bool? Reasoning, bool? Vision, bool? AudioInput, bool? VideoInput,
    IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities);

/// <summary>
/// Nhóm model theo provider trong bảng chọn của trang Tools.
/// <see cref="IsFreePreset"/> = preset OpenCode Free (nhận diện theo BaseUrl canonical) — chỉ nhóm này được tick mặc định (C4/C7).
/// </summary>
/// <param name="ProviderName">Tên provider (tiêu đề nhóm).</param>
/// <param name="ProviderEnabled"><see langword="false"/> → UI hiện badge "provider đang tắt"; model trong nhóm không tick mặc định (C7).</param>
/// <param name="IsFreePreset">True khi provider là preset OpenCode Free.</param>
/// <param name="Models">Model đang Enabled của provider, order theo Id.</param>
public sealed record ToolsModelGroup(
    string ProviderName, bool ProviderEnabled, bool IsFreePreset, IReadOnlyList<ToolsModelRow> Models);

/// <summary>Dữ liệu trang Tools: các nhóm provider/model + tick mặc định (C7).</summary>
/// <param name="Groups">Nhóm hiển thị trên bảng chọn, order theo Id provider.</param>
/// <param name="DefaultSelectedIds">Id model tick sẵn khi mở trang — model Enabled của preset OpenCode Free khi provider đang bật (C7).</param>
public sealed record OpenCodeConfigPageData(
    IReadOnlyList<ToolsModelGroup> Groups, IReadOnlyList<long> DefaultSelectedIds);

/// <summary>Kết quả sinh config opencode.</summary>
/// <param name="Json">JSON config (indent) để preview/copy/download.</param>
/// <param name="SkippedDuplicateModelIds">ModelId bị bỏ khỏi JSON do trùng key — UI ghi chú dưới preview.</param>
public sealed record OpenCodeConfigResult(string Json, IReadOnlyList<string> SkippedDuplicateModelIds);

/// <summary>Service trang Tools: đọc DB dựng bảng chọn + sinh config opencode v2.</summary>
public interface IOpenCodeConfigService
{
    /// <summary>Dựng dữ liệu bảng chọn model từ DB (nhóm theo provider, chỉ model Enabled).</summary>
    Task<OpenCodeConfigPageData> GetPageDataAsync(CancellationToken ct = default);

    /// <summary>Sinh config opencode v2 từ các model người dùng tick.</summary>
    /// <param name="selectedModelIds">Id entity của model được tick.</param>
    /// <param name="baseUrl">Base URL endpoint proxy của app (xem <see cref="OpenCodeConfigService.BuildBaseUrl"/>).</param>
    /// <param name="apiKey">API key client gửi kèm trong config; <see langword="null"/>/rỗng → không emit.</param>
    /// <param name="ct">Token hủy.</param>
    Task<OpenCodeConfigResult> GenerateAsync(
        IReadOnlyList<long> selectedModelIds, string baseUrl, string? apiKey, CancellationToken ct = default);
}
