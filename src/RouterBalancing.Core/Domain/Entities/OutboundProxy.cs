namespace RouterBalancing.Core.Domain;

/// <summary>
/// Proxy outbound toàn cục — mọi request tới provider đi qua pool (spec proxy-pool §3.1).
/// Tên đầy đủ (không phải <c>Proxy</c> thuần) để không trỏng với ProxyHost/ProxyApp/ProxyRequest.
/// Không persist runtime down-state — health sống in-memory ở <c>ProxyPool</c>.
/// </summary>
public class OutboundProxy
{
    public long Id { get; set; }

    /// <summary><c>"http"</c> hoặc <c>"socks5"</c> — validate ở <c>ProxyValidator</c>, luôn lưu lowercase.</summary>
    public string Scheme { get; set; } = "http";

    /// <summary>Hostname/IP của proxy — bắt buộc, không rỗng.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Cổng 1–65535.</summary>
    public int Port { get; set; }

    /// <summary>Tên đăng nhập proxy — null = không auth.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password đã DPAPI qua <see cref="Security.ISecretProtector"/> (pattern ProviderAccount.ApiKeyEncrypted)
    /// — không bao giờ log/plaintext. Invariant: != null ⇒ <see cref="Username"/> != null.
    /// </summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>Tắt để giữ proxy mà không dùng trong pool — default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Kết quả test echo lần cuối — null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Thông điệp kết quả/lỗi (không chứa credentials).</summary>
    public string? LastTestMessage { get; set; }

    /// <summary>IP egress echo trả về.</summary>
    public string? LastTestIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
