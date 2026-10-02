namespace RouterBalancing.Core.Domain;

/// <summary>Nhà cung cấp LLM mà proxy chuyển tiếp request tới.</summary>
public class Provider
{
    public long Id { get; set; }

    /// <summary>Tên hiển thị do người dùng đặt.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mã định danh slug (unique) — client pin provider bằng "{Identifier}/{ModelId}". Nullable chỉ để migration an toàn; backfill tự chạy khi khởi động.</summary>
    public string? Identifier { get; set; }

    public ProviderType Type { get; set; }

    /// <summary>Đảo gốc API, ví dụ <c>https://api.openai.com</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Số request đồng thời tối đa — kích thước Execution List.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Kết quả test connection gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    public string? LastTestMessage { get; set; }

    /// <summary>Provider preset từ free catalog — seed lúc khởi động, không cho xóa (spec provider-free §5.1).</summary>
    public bool IsPreset { get; set; }

    /// <summary>Lần sync model free gần nhất — null = chưa sync (tách khỏi LastTestAt: 2 việc khác nhau).</summary>
    public DateTimeOffset? LastModelSyncAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Model> Models { get; set; } = [];

    /// <summary>Proxy outbound gán cho provider (M2M). Rỗng = chưa gán → direct (D2).</summary>
    public List<ProviderProxy> ProviderProxies { get; set; } = [];

    /// <summary>Mode dùng tập proxy trên; null = kế thừa từ account (nếu có) hoặc RoundRobin.</summary>
    public ProxyMode? ProxyMode { get; set; }

    /// <summary>Tài khoản (API key) thuộc provider — cascade khi xoá provider.</summary>
    public List<ProviderAccount> Accounts { get; set; } = [];
}
