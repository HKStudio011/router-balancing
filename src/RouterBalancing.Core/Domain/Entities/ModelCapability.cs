using RouterBalancing.Core.Capabilities;

namespace RouterBalancing.Core.Domain;

/// <summary>
/// Capability (tools/reasoning) của một model theo đúng một nguồn (spec capability-model §6).
/// Không có dòng = chưa biết; dòng <c>KnownAbsent=1</c> = nguồn đã biết khẳng định KHÔNG có (C-I).
/// </summary>
public class ModelCapability
{
    /// <summary>FK → Models(Id), một phần PK ghép; xóa model cascade xóa dòng.</summary>
    public long ModelId { get; set; }

    /// <summary>Loại capability — phần PK ghép, DB lưu TEXT chữ thường qua <see cref="CapabilityNames"/>.</summary>
    public CapabilityKind Capability { get; set; }

    /// <summary>true = nguồn full-list khẳng định model không có capability (C-P); badge "không có" (C-I).</summary>
    public bool KnownAbsent { get; set; }

    /// <summary>Nguồn đã sinh dòng — quyết định quyền ghi đè (C-E/C-N).</summary>
    public CapabilitySource Source { get; set; }

    /// <summary>Thời điểm dòng được ghi gần nhất (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
