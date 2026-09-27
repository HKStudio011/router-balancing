namespace RouterBalancing.Core.Domain;

/// <summary>Nhà cung cấp LLM mà proxy chuyển tiếp request tới.</summary>
public class Provider
{
    public long Id { get; set; }

    /// <summary>Tên hiển thị do người dùng đặt.</summary>
    public string Name { get; set; } = string.Empty;

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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Model> Models { get; set; } = [];

    /// <summary>Tài khoản (API key) thuộc provider — cascade khi xoá provider.</summary>
    public List<ProviderAccount> Accounts { get; set; } = [];
}
