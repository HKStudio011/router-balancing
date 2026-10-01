namespace RouterBalancing.Core.Domain;

/// <summary>
/// API key inbound của client gọi proxy - chỉ lưu SHA-256 (KeyHash), không lưu plaintext.
/// Counter Requests/Tokens reset lười theo <see cref="UsageDate"/> (ngày UTC).
/// </summary>
public class ClientKey
{
    public long Id { get; set; }

    /// <summary>Tên tag client, hiển thị trong bảng UI - không cần duy nhất.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (64 ký tự) của key plaintext - unique index.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Mask hiển thị (vd <c>sk-rb-…Ab12</c>) - hash không suy ra được 4 ký tự cuối.</summary>
    public string KeyMask { get; set; } = string.Empty;

    /// <summary>Revoke mềm: tắt vẫn giữ row, chỉ không match ở middleware.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Counter request trong ngày UTC <see cref="UsageDate"/>.</summary>
    public long RequestsUsed { get; set; }

    /// <summary>Counter token (prompt+completion) trong ngày UTC <see cref="UsageDate"/>.</summary>
    public long TokensUsed { get; set; }

    /// <summary>Ngày UTC của 2 counter trên - lệch ngày thì reset tại lần ghi đầu tiên.</summary>
    public DateOnly? UsageDate { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>RPM; null = không giới hạn.</summary>
    public int? RatePerMinute { get; set; }

    /// <summary>TPM; null = không giới hạn.</summary>
    public int? TokensPerMinute { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
