namespace RouterBalancing.Core.Domain;

/// <summary>Combo router — client chọn qua trường <c>model</c> của request.</summary>
public class Combo
{
    public long Id { get; set; }

    /// <summary>Tên combo = giá trị trường <c>model</c> client gửi lên.</summary>
    public string Name { get; set; } = string.Empty;

    public ComboMode Mode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ComboItem> Items { get; set; } = [];
}
