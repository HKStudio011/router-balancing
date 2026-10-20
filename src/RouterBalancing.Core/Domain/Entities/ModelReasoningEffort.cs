using RouterBalancing.Core.Capabilities;

namespace RouterBalancing.Core.Domain;

/// <summary>
/// Một mức reasoning effort đã biết của model theo đúng một nguồn (spec capability-model §6).
/// Không có dòng = chưa biết (C-I); bảng không có KnownAbsent — negative cho reasoning
/// nằm ở dòng <see cref="ModelCapability"/>(reasoning) với KnownAbsent=1.
/// </summary>
public class ModelReasoningEffort
{
    /// <summary>FK → Models(Id), một phần PK ghép; xóa model cascade xóa dòng.</summary>
    public long ModelId { get; set; }

    /// <summary>
    /// Mức effort ("low"/"medium"/"high"…) — phần PK ghép, TEXT. Giá trị tự do vì mỗi
    /// provider đặt tên mức khác nhau; không khóa bằng enum để không mất dữ liệu provider lạ.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Nguồn đã sinh dòng — quyết định quyền ghi đè (C-E/C-N).</summary>
    public CapabilitySource Source { get; set; }

    /// <summary>Thời điểm dòng được ghi gần nhất (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
