using System.ComponentModel.DataAnnotations.Schema;

namespace RouterBalancing.Core.Domain;

/// <summary>
/// Tài khoản (API key) thuộc một nhà cung cấp — đa tài khoản, key sống hoàn toàn ở đây.
/// Usage ngày được lưu trong <see cref="UsageDaily"/>, không còn counter trên bảng này.
/// </summary>
public class ProviderAccount
{
    /// <summary>Khóa chính — EF tự sinh khi tạo tài khoản.</summary>
    public long Id { get; set; }

    /// <summary>FK sang provider sở hữu; xóa provider cascade xóa toàn bộ account.</summary>
    public long ProviderId { get; set; }

    /// <summary>Navigation về provider sở hữu tài khoản.</summary>
    public Provider Provider { get; set; } = null!;

    /// <summary>Tên hiển thị — unique trong cùng provider.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>API key mã hoá DPAPI — không bao giờ lưu/log plaintext.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    /// <summary>Tắt để giữ key nhưng ngừng dùng — failover bỏ qua account đã tắt.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>JSON array of string pattern; <see langword="null"/>/rỗng = match mọi model (Phase 3).</summary>
    public string? ModelPatterns { get; set; }

    /// <summary>Trọng số chia tải khi nhiều key cùng match (Phase 3). 0–10000, mặc định 100.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Giới hạn token mỗi ngày UTC; <see langword="null"/> = không giới hạn (enforcement là follow-up).</summary>
    public int? DailyTokenLimit { get; set; }

    /// <summary>Giới hạn request mỗi ngày UTC; <see langword="null"/> = không giới hạn (enforcement là follow-up).</summary>
    public int? DailyRequestLimit { get; set; }

    /// <summary>
    /// Token đã dùng hôm nay (UTC) — chỉ để hiển thị, nạp từ <see cref="UsageDaily"/>
    /// trong <c>ProviderAccountService.ListAsync</c>; không phải cột DB.
    /// </summary>
    [NotMapped]
    public long TokensUsed { get; set; }

    /// <summary>
    /// Request đã dùng hôm nay (UTC) — chỉ để hiển thị, nạp từ <see cref="UsageDaily"/>
    /// trong <c>ProviderAccountService.ListAsync</c>; không phải cột DB.
    /// </summary>
    [NotMapped]
    public long RequestsUsed { get; set; }

    /// <summary>Kết quả test gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    /// <summary>Thời điểm test key gần nhất (UTC); <see langword="null"/> = chưa test.</summary>
    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Lời nhắn kết quả test gần nhất; <see langword="null"/> = chưa test.</summary>
    public string? LastTestMessage { get; set; }

    /// <summary>Thời điểm tạo tài khoản (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Thời điểm cập nhật gần nhất (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Proxy outbound gán cho account (override provider). Ý nghĩa theo <see cref="ProxyOverrideEnabled"/>:
    /// flag=false = hàng không đọc (kế thừa tập provider); flag=true + rỗng = direct.
    /// </summary>
    public List<ProviderAccountProxy> AccountProxies { get; set; } = [];

    /// <summary>Mode dùng tập proxy trên; null = kế thừa provider.</summary>
    public ProxyMode? ProxyMode { get; set; }

    /// <summary>true = AC dùng tập proxy riêng của mình (rỗng = direct); false = kế thừa provider.</summary>
    public bool ProxyOverrideEnabled { get; set; }
}
