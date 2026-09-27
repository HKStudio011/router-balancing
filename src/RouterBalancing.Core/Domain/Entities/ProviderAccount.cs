namespace RouterBalancing.Core.Domain;

/// <summary>Tài khoản (API key) thuộc một nhà cung cấp — đa tài khoản, key sống hoàn toàn ở đây.</summary>
public class ProviderAccount
{
    public long Id { get; set; }

    public long ProviderId { get; set; }

    public Provider Provider { get; set; } = null!;

    /// <summary>Tên hiển thị — unique trong cùng provider.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>API key mã hoá DPAPI — không bao giờ lưu/log plaintext.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>JSON array of string pattern; <see langword="null"/>/rỗng = match mọi model (Phase 3).</summary>
    public string? ModelPatterns { get; set; }

    /// <summary>Trọng số chia tải khi nhiều key cùng match (Phase 3). 0–10000, mặc định 100.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Thứ tự failover — nhỏ hơn = dùng trước; −1000–1000, mặc định 0 (Phase 3).</summary>
    public int Priority { get; set; }

    public int? DailyTokenLimit { get; set; }

    public long TokensUsed { get; set; }

    public int? DailyRequestLimit { get; set; }

    public long RequestsUsed { get; set; }

    /// <summary>Ngày UTC của 2 counter trên — reset khi sang ngày (Phase 3 ghi).</summary>
    public DateOnly? UsageDate { get; set; }

    /// <summary>Kết quả test gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
