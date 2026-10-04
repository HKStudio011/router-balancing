namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bản nháp form account. Property mutable (không phải positional record)
/// để Blazor <c>@bind</c> ghi được — giống <c>ProviderDraft</c>.
/// </summary>
public sealed class ProviderAccountDraft
{
    /// <summary>Provider sở hữu account — service check tồn tại trước khi ghi.</summary>
    public long ProviderId { get; set; }

    /// <summary>Tên hiển thị, tối đa 100 ký tự, duy nhất trong cùng provider (sau khi Trim).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Plaintext lúc nhập; rỗng hoặc toàn khoảng trắng khi update = giữ key đã lưu. Không bao giờ log/persist trực tiếp.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Không dùng API key (free endpoint): Create/Update ghi cột key rỗng,
    /// bỏ qua <see cref="ApiKey"/> (spec free-account D5).</summary>
    public bool NoKey { get; set; }

    /// <summary>Tắt = account bị loại khỏi chọn traffic và TestAll, không xoá key.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pattern do UI gửi (1 phần tử = 1 dòng textarea); service serialize JSON.</summary>
    public string[] ModelPatterns { get; set; } = [];

    /// <summary>Trọng số chia tải (0–10000); lớn = nhận nhiều request hơn khi bình đẳng.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Giới hạn token/ngày; null = không giới hạn; phải &gt; 0 nếu đặt.</summary>
    public int? DailyTokenLimit { get; set; }

    /// <summary>Giới hạn request/ngày; null = không giới hạn; phải &gt; 0 nếu đặt.</summary>
    public int? DailyRequestLimit { get; set; }
}
