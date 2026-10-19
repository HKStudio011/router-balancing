namespace RouterBalancing.Core.Capabilities;

/// <summary>
/// Tập phát biểu capability của một model từ một nguồn — đầu vào duy nhất của
/// <see cref="IModelCapabilityWriter"/> (spec capability-model §5).
/// Giá trị <c>false</c> trong dictionary = "đã biết là không" (KnownAbsent);
/// key có mặt trong dictionary = nguồn đó có phát biểu về mục đó. Key không có
/// trong patch = không phát biểu gì — writer không được đụng tới dòng sẵn có.
/// </summary>
/// <param name="Capabilities">Phát biểu tools/reasoning (value false = KnownAbsent).</param>
/// <param name="Modalities">Phát biểu modality theo chiều input/output (value false = KnownAbsent).</param>
/// <param name="ReasoningEfforts">Các mức effort model hỗ trợ — chỉ khẳng định thuận:
/// bảng effort không có KnownAbsent, negative của reasoning nằm ở
/// <c>Capabilities[Reasoning]=false</c>.</param>
public sealed record CapabilityPatch(
    IReadOnlyDictionary<CapabilityKind, bool> Capabilities,
    IReadOnlyDictionary<(ModalityDirection Direction, ModalityKind Modality), bool> Modalities,
    IReadOnlySet<string> ReasoningEfforts)
{
    /// <summary>Định danh namespace trong <see cref="ClearKeys"/> cho dòng ModelCapability.</summary>
    public const string CapabilityKeyKind = "capability";

    /// <summary>Định danh namespace trong <see cref="ClearKeys"/> cho dòng ModelModality.</summary>
    public const string ModalityKeyKind = "modality";

    /// <summary>Định danh namespace trong <see cref="ClearKeys"/> cho dòng ModelReasoningEffort.</summary>
    public const string EffortKeyKind = "effort";

    private static readonly IReadOnlySet<(string Kind, string Key)> NoClearKeys = new HashSet<(string, string)>();

    /// <summary>
    /// Các key người dùng muốn XÓA phát biểu (trả về "chưa rõ") — chỉ có nghĩa với
    /// <see cref="IModelCapabilityWriter.ApplyUserEditAsync"/>. Dạng (Kind, Key):
    /// <c>(CapabilityKeyKind, "tools" | "reasoning")</c>,
    /// <c>(ModalityKeyKind, "input/image"…)</c> — token qua <see cref="CapabilityNames"/>,
    /// <c>(EffortKeyKind, "&lt;level&gt;")</c>. Dùng chuỗi thay key tuple của dictionary
    /// ở trên để một bộ key thống nhất cho cả ba bảng (dùng helper
    /// <see cref="CapabilityClearKey"/> / <see cref="ModalityClearKey"/> /
    /// <see cref="EffortClearKey"/> để tự ghép token).
    /// </summary>
    public IReadOnlySet<(string Kind, string Key)> ClearKeys { get; init; } = NoClearKeys;

    /// <summary>Tạo ClearKey cho dòng ModelCapability của <paramref name="kind"/>.</summary>
    public static (string Kind, string Key) CapabilityClearKey(CapabilityKind kind) =>
        (CapabilityKeyKind, kind.ToDb());

    /// <summary>Tạo ClearKey cho dòng ModelModality theo chiều và loại.</summary>
    public static (string Kind, string Key) ModalityClearKey(ModalityDirection direction, ModalityKind modality) =>
        (ModalityKeyKind, $"{direction.ToDb()}/{modality.ToDb()}");

    /// <summary>
    /// Tạo ClearKey cho dòng ModelReasoningEffort. Level được writer chuẩn hoá
    /// (trim + lowercase invariant) trước khi so — người dùng gõ hoa/thường đều xóa đúng dòng.
    /// </summary>
    public static (string Kind, string Key) EffortClearKey(string level) => (EffortKeyKind, level);
}

/// <summary>
/// Lối ghi DUY NHẤT vào ba bảng fact capability (ModelCapabilities / ModelModalities /
/// ModelReasoningEfforts). Mọi consumer (importer catalog, probe endpoint, UI) phải ghi
/// qua interface này — không tự Add/Update entity fact.
/// </summary>
public interface IModelCapabilityWriter
{
    /// <summary>
    /// Áp phát biểu của một nguồn máy móc (catalog/endpoint).
    /// Luật (a): chỉ ghi khi dòng chưa tồn tại hoặc đang mang đúng
    /// <paramref name="source"/> đó — nguồn khác không đè được.
    /// Luật (b): dòng KnownAbsent=1 chỉ được ghi khi
    /// <paramref name="sourceIsCompleteList"/>=true.
    /// </summary>
    /// <param name="modelId">PK <c>Models.Id</c>.</param>
    /// <param name="source">Nguồn phát biểu (User nên đi qua <see cref="ApplyUserEditAsync"/>).</param>
    /// <param name="patch">Các phát biểu; key không có trong patch giữ nguyên.</param>
    /// <param name="sourceIsCompleteList">
    /// true khi nguồn là full-list — mọi mục không được nêu trong patch THẬT SỰ
    /// model không hỗ trợ, nên mới được phép sinh negative (C-P).
    /// </param>
    Task ApplyAsync(long modelId, CapabilitySource source, CapabilityPatch patch,
        bool sourceIsCompleteList, CancellationToken ct = default);

    /// <summary>
    /// Người dùng đặt tay — nguồn có quyền cao nhất (C-N).
    /// Luật (c): với mỗi key trong patch (kể cả <see cref="CapabilityPatch.ClearKeys"/>),
    /// xóa dòng cũ bất kể nguồn rồi ghi dòng <c>Source=User</c>; ClearKeys chỉ xóa,
    /// không ghi lại. Key không có trong patch → giữ nguyên.
    /// Negative từ user (value=false) luôn được ghi — user khẳng định trực tiếp,
    /// không cần full-list.
    /// </summary>
    Task ApplyUserEditAsync(long modelId, CapabilityPatch patch, CancellationToken ct = default);

    /// <summary>Xóa toàn bộ dòng fact của model do đúng nguồn đó sinh ra (3 bảng).</summary>
    Task ClearSourceAsync(long modelId, CapabilitySource source, CancellationToken ct = default);

    /// <summary>Xóa mọi dòng fact của model bất kể nguồn (3 bảng).</summary>
    Task ClearAllAsync(long modelId, CancellationToken ct = default);
}
