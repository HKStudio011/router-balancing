namespace RouterBalancing.Core.Engine;

/// <summary>
/// Kết quả 3 nghĩa của <see cref="IExecutionList.TryEnterAsync"/> (spec §2.2) —
/// dispatcher phân biệt được "vào được" / "hết chỗ (park)" / "hết TK chưa thử (advance)"
/// thay vì đoán từ <c>long?</c> với snapshot có thể stale.
/// </summary>
public abstract record TryEnterResult
{
    /// <summary>Vào được — <c>AccountId</c> đã reserve; 0 = sentinel khi provider không có TK enabled (V1).</summary>
    public sealed record Entered(long AccountId) : TryEnterResult;

    /// <summary>Còn TK chưa thử nhưng hết capacity, hoặc provider không tồn tại → park chờ <c>Exited</c> (D-B4/D-B7).</summary>
    public sealed record Full : TryEnterResult;

    /// <summary>Mọi TK enabled của provider đều nằm trong <c>excludedAccounts</c> → advance candidate (§2.2).</summary>
    public sealed record NoAccountLeft : TryEnterResult;
}
