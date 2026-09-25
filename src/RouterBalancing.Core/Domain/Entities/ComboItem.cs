namespace RouterBalancing.Core.Domain;

/// <summary>Dòng trong combo — trỏ tới model hoặc combo con (đúng 1 trong 2).</summary>
public class ComboItem
{
    public long Id { get; set; }

    public long ComboId { get; set; }

    /// <summary>Thứ tự — có ý nghĩa với mode Fallback.</summary>
    public int Position { get; set; }

    public long? TargetModelId { get; set; }

    /// <summary>Combo lồng nhau — phải validate cycle khi lưu.</summary>
    public long? TargetComboId { get; set; }
}
