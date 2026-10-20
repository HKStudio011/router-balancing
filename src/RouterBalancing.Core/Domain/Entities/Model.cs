namespace RouterBalancing.Core.Domain;

/// <summary>Một model thuộc provider — đơn vị được chọn khi cân bằng tải.</summary>
public class Model
{
    public long Id { get; set; }

    public long ProviderId { get; set; }

    public Provider? Provider { get; set; }

    /// <summary>ID model phía provider, ví dụ <c>gpt-4o-mini</c>.</summary>
    public string ModelId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Active/deactive — model tắt không tham gia selection.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True khi người dùng thêm tay thay vì auto-fetch.</summary>
    public bool IsManual { get; set; }

    public int? ContextWindow { get; set; }

    /// <summary>Giới hạn token output; null = chưa rõ.</summary>
    public int? MaxOutputTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Dòng capability (tools/reasoning) per nguồn — xóa model cascade xóa hết fact (spec §6).</summary>
    public ICollection<ModelCapability> ModelCapabilities { get; set; } = [];

    /// <summary>Dòng modality input/output per nguồn — cascade như <see cref="ModelCapabilities"/>.</summary>
    public ICollection<ModelModality> ModelModalities { get; set; } = [];

    /// <summary>Mức reasoning effort đã biết per nguồn — cascade như <see cref="ModelCapabilities"/>.</summary>
    public ICollection<ModelReasoningEffort> ModelReasoningEfforts { get; set; } = [];
}
