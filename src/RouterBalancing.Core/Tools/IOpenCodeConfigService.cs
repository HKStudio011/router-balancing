using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Tools;

/// <summary>
/// Một model trong bảng chọn của trang Tools — dữ liệu đủ để render row, không mang entity EF.
/// Capability đến từ <c>ModelCapabilityView</c>: ba trạng thái —
/// <see langword="null"/> = chưa biết (không có fact), <see langword="false"/> = đã biết là không.
/// </summary>
/// <param name="Id">Id entity — value của checkbox chọn model.</param>
/// <param name="ModelId">Model id phía provider.</param>
/// <param name="DisplayName">Tên hiển thị; <see langword="null"/> → UI/generator fallback về <paramref name="ModelId"/>.</param>
/// <param name="ContextWindow">Context window (token); <see langword="null"/> = không rõ.</param>
/// <param name="Tools">Model gọi tool được (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="Reasoning">Model suy nghĩ được (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="Vision">Model nhận ảnh (badge UI); <see langword="null"/> = chưa biết.</param>
/// <param name="AudioInput">Model nhận audio đầu vào (badge media); <see langword="null"/> = chưa biết.</param>
/// <param name="VideoInput">Model nhận video đầu vào (badge media); <see langword="null"/> = chưa biết.</param>
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
/// <param name="ProviderId">Id provider — khóa lưu trạng thái thu/giãn ở AppSettings (C-G).</param>
/// <param name="ProviderName">Tên provider (tiêu đề nhóm).</param>
/// <param name="ProviderEnabled"><see langword="false"/> → UI hiện badge "provider đang tắt"; model trong nhóm không tick mặc định (C7).</param>
/// <param name="IsFreePreset">True khi provider là preset OpenCode Free.</param>
/// <param name="Models">Model đang Enabled của provider, order theo Id.</param>
public sealed record ToolsModelGroup(
    long ProviderId, string ProviderName, bool ProviderEnabled, bool IsFreePreset, IReadOnlyList<ToolsModelRow> Models);

/// <summary>
/// Một combo trong bảng chọn của trang Tools — U12: tick combo = một entry config key = tên combo.
/// Capability là giao an toàn của các member (C-L); ba trạng thái như <see cref="ToolsModelRow"/>.
/// </summary>
/// <param name="Id">Id combo — value của checkbox chọn combo.</param>
/// <param name="Name">Tên combo — cũng là key/tên trong config opencode.</param>
/// <param name="Mode">Mode định tuyến (RR/FB).</param>
/// <param name="MemberCount">Số item trực tiếp trong combo (model hoặc combo con).</param>
/// <param name="Tools">Tools (AND qua member); <see langword="null"/> = chưa biết.</param>
/// <param name="Reasoning">Reasoning (AND qua member); <see langword="null"/> = chưa biết.</param>
/// <param name="Vision">Vision input (AND qua member); <see langword="null"/> = chưa biết.</param>
/// <param name="Audio">Audio input (AND qua member); <see langword="null"/> = chưa biết.</param>
/// <param name="Video">Video input (AND qua member); <see langword="null"/> = chưa biết.</param>
/// <param name="ContextWindow">Context nhỏ nhất của member; <see langword="null"/> = có member chưa rõ.</param>
/// <param name="InputModalities">Giao modality input khẳng định thuận; <see langword="null"/> = chưa rõ.</param>
/// <param name="OutputModalities">Giao modality output khẳng định thuận; <see langword="null"/> = chưa rõ.</param>
public sealed record ToolsComboRow(
    long Id, string Name, ComboMode Mode, int MemberCount,
    bool? Tools, bool? Reasoning, bool? Vision, bool? Audio, bool? Video, int? ContextWindow,
    IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities);

/// <summary>Dữ liệu trang Tools: các nhóm provider/model + combo + tick mặc định (C7).</summary>
/// <param name="Groups">Nhóm hiển thị trên bảng chọn, order theo Id provider.</param>
/// <param name="Combos">Combo của app, order theo Name.</param>
/// <param name="DefaultSelectedIds">Id model tick sẵn khi mở trang — model Enabled của preset OpenCode Free khi provider đang bật (C7).</param>
public sealed record OpenCodeConfigPageData(
    IReadOnlyList<ToolsModelGroup> Groups, IReadOnlyList<ToolsComboRow> Combos, IReadOnlyList<long> DefaultSelectedIds);

/// <summary>Kết quả sinh config opencode.</summary>
/// <param name="Json">JSON config (indent) để preview/copy/download.</param>
/// <param name="SkippedKeys">Key bị bỏ khỏi JSON do trùng (model trùng key hoặc combo trùng key model) — UI ghi chú dưới preview.</param>
public sealed record OpenCodeConfigResult(string Json, IReadOnlyList<string> SkippedKeys);

/// <summary>Service trang Tools: đọc DB dựng bảng chọn + sinh config opencode v2.</summary>
public interface IOpenCodeConfigService
{
    /// <summary>Dựng dữ liệu bảng chọn model/combo từ DB (nhóm theo provider, chỉ model Enabled).</summary>
    Task<OpenCodeConfigPageData> GetPageDataAsync(CancellationToken ct = default);

    /// <summary>Sinh config opencode v2 từ các model/combo người dùng tick.</summary>
    /// <param name="selectedModelIds">Id entity của model được tick.</param>
    /// <param name="selectedComboIds">Id entity của combo được tick.</param>
    /// <param name="baseUrl">Base URL endpoint proxy của app (xem <see cref="OpenCodeConfigService.BuildBaseUrl"/>).</param>
    /// <param name="apiKey">API key client gửi kèm trong config; <see langword="null"/>/rỗng → không emit.</param>
    /// <param name="ct">Token hủy.</param>
    Task<OpenCodeConfigResult> GenerateAsync(
        IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds,
        string baseUrl, string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// Ghi block <c>providers["router-balancing"]</c> vào file config opencode (spec §9.8):
    /// xoá block cũ trong <c>provider</c>/<c>providers</c>, chèn block mới, giữ nguyên phần khác.
    /// </summary>
    /// <param name="path">Đường dẫn file config (đã expand <c>~</c>; xem <c>OpenCodeConfigPaths</c>).</param>
    /// <param name="selectedModelIds">Id entity của model được tick.</param>
    /// <param name="selectedComboIds">Id entity của combo được tick.</param>
    /// <param name="baseUrl">Base URL endpoint proxy của app.</param>
    /// <param name="apiKey">API key client gửi kèm; <see langword="null"/>/rỗng → không emit.</param>
    /// <param name="ct">Token hủy.</param>
    /// <exception cref="OpenCodeConfigFormatException">File hiện có không phải JSON object hợp lệ.</exception>
    Task<OpenCodeConfigWriteResult> WriteAsync(
        string path, IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds,
        string baseUrl, string? apiKey, CancellationToken ct = default);
}
