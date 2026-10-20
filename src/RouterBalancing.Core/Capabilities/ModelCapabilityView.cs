namespace RouterBalancing.Core.Capabilities;

/// <summary>
/// Bản đọc hợp nhất từ ba bảng fact capability cho MỘT model (spec capability-model §7).
/// Đây là kiểu duy nhất mọi consumer (generator config, combo calculator, badge UI,
/// /v1/models) dùng để biết capability của model — không ai tự query entity fact.
/// Quy tắc ngữ nghĩa (C-I): key có trong dictionary = đã biết (true = hỗ trợ,
/// false = KnownAbsent); key không có = chưa biết, tra ra <see langword="null"/>.
/// View không bao giờ bịa phát biểu: không có dòng nào thì dictionary/set rỗng và
/// consumer phải hiểu rỗng là "chưa biết", không phải "biết là không có".
/// </summary>
/// <param name="Capabilities">Phát biểu tools/reasoning (value false = KnownAbsent).</param>
/// <param name="Modalities">Phát biểu modality theo chiều input/output (value false = KnownAbsent).</param>
/// <param name="ReasoningEfforts">Các mức effort model HỖ TRỢ (R-T2-2): set rỗng = chưa biết,
/// không phải "biết là không có mức nào" — negative của reasoning nằm ở
/// <c>Capabilities[Reasoning]=false</c>.</param>
public sealed record ModelCapabilityView(
    IReadOnlyDictionary<CapabilityKind, bool> Capabilities,
    IReadOnlyDictionary<(ModalityDirection Direction, ModalityKind Modality), bool> Modalities,
    IReadOnlySet<string> ReasoningEfforts)
{
    /// <summary>View không có bất kỳ phát biểu nào — model chưa có dòng fact (reader trả về cho model trống).</summary>
    public static ModelCapabilityView Empty { get; } = new(
        new Dictionary<CapabilityKind, bool>(),
        new Dictionary<(ModalityDirection, ModalityKind), bool>(),
        new HashSet<string>());

    /// <summary>Model hỗ trợ tools; <see langword="null"/> = chưa biết (không có dòng fact).</summary>
    public bool? Tools => Get(Capabilities, CapabilityKind.Tools);

    /// <summary>Model hỗ trợ reasoning; <see langword="null"/> = chưa biết.</summary>
    public bool? Reasoning => Get(Capabilities, CapabilityKind.Reasoning);

    /// <summary>Phát biểu modality theo chiều; <see langword="null"/> = chưa biết.</summary>
    public bool? Supports(ModalityDirection direction, ModalityKind modality) =>
        Modalities.TryGetValue((direction, modality), out var known) ? known : null;

    /// <summary>Model nhận ảnh đầu vào; <see langword="null"/> = chưa biết.</summary>
    public bool? Vision => Supports(ModalityDirection.Input, ModalityKind.Image);

    /// <summary>Model nhận audio đầu vào; <see langword="null"/> = chưa biết.</summary>
    public bool? AudioInput => Supports(ModalityDirection.Input, ModalityKind.Audio);

    /// <summary>Model nhận video đầu vào; <see langword="null"/> = chưa biết.</summary>
    public bool? VideoInput => Supports(ModalityDirection.Input, ModalityKind.Video);

    /// <summary>Model phát audio đầu ra; <see langword="null"/> = chưa biết.</summary>
    public bool? AudioOutput => Supports(ModalityDirection.Output, ModalityKind.Audio);

    /// <summary>Các modality input được khẳng định thuận, theo ModalityKind tăng dần.</summary>
    public IReadOnlyList<string> InputModalities => ModalitiesOf(ModalityDirection.Input);

    /// <summary>Các modality output được khẳng định thuận, theo ModalityKind tăng dần.</summary>
    public IReadOnlyList<string> OutputModalities => ModalitiesOf(ModalityDirection.Output);

    /// <summary>
    /// Các mức effort đã biết, sort ordinal. Tách khỏi <see cref="ReasoningEfforts"/> (set,
    /// dùng cho test membership) vì brief đặt trùng tên hai member — C# không cho phép;
    /// bản sort tách tên để record shape (positional set) giữ đúng hợp đồng construction.
    /// </summary>
    public IReadOnlyList<string> SortedReasoningEfforts =>
        ReasoningEfforts.OrderBy(e => e, StringComparer.Ordinal).ToArray();

    /// <summary>Model chưa có bất kỳ phát biểu capability nào — UI/badge hiển thị "không rõ".</summary>
    public bool IsEmpty => Capabilities.Count == 0 && Modalities.Count == 0 && ReasoningEfforts.Count == 0;

    private static bool? Get<TKey>(IReadOnlyDictionary<TKey, bool> map, TKey key) =>
        map.TryGetValue(key, out var value) ? value : null;

    // Thứ tự ModalityKind tăng dần là hợp đồng với generator config: hai lần generate
    // cùng dữ liệu phải ra byte-for-byte giống nhau (test so sánh list không ổn định
    // nếu thứ tự phụ thuộc thứ tự dòng trả về từ DB).
    private IReadOnlyList<string> ModalitiesOf(ModalityDirection direction) =>
        Modalities
            .Where(kv => kv.Key.Direction == direction && kv.Value)
            .OrderBy(kv => kv.Key.Modality)
            .Select(kv => kv.Key.Modality.ToDb())
            .ToArray();
}
