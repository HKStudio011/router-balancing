using RouterBalancing.Core.Capabilities;

namespace RouterBalancing.Core.Domain;

/// <summary>
/// Modality input/output của một model theo đúng một nguồn (spec capability-model §6).
/// Vision/audio/video không còn là capability — chỉ là modality (C-H); đối xứng
/// <see cref="ModelCapability"/> về KnownAbsent (C-O).
/// </summary>
public class ModelModality
{
    /// <summary>FK → Models(Id), một phần PK ghép; xóa model cascade xóa dòng.</summary>
    public long ModelId { get; set; }

    /// <summary>Chiều so với request — phần PK ghép, DB lưu TEXT "input"/"output".</summary>
    public ModalityDirection Direction { get; set; }

    /// <summary>Loại modality — phần PK ghép, DB lưu TEXT "text"/"image"/"audio"/"video".</summary>
    public ModalityKind Modality { get; set; }

    /// <summary>true = nguồn full-list khẳng định model không có modality này ở chiều đó (C-O).</summary>
    public bool KnownAbsent { get; set; }

    /// <summary>Nguồn đã sinh dòng — quyết định quyền ghi đè (C-E/C-N).</summary>
    public CapabilitySource Source { get; set; }

    /// <summary>Thời điểm dòng được ghi gần nhất (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
