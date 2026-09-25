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

    public bool SupportsVision { get; set; }

    public bool SupportsThink { get; set; }

    /// <summary>JSON array mức think effort, ví dụ <c>["low","high"]</c>; null = không rõ.</summary>
    public string? ThinkEfforts { get; set; }

    /// <summary>JSON array modalities đầu vào, ví dụ <c>["text","image"]</c>.</summary>
    public string? InputModalities { get; set; }

    /// <summary>JSON array modalities đầu ra, ví dụ <c>["text"]</c>.</summary>
    public string? OutputModalities { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
