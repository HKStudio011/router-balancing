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

    /// <summary>Plaintext lúc nhập; rỗng khi update = giữ key đã lưu. Không bao giờ log/persist trực tiếp.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Tắt = account bị loại khỏi chọn traffic và TestAll, không xoá key.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pattern do UI gửi (1 phần tử = 1 dòng textarea); service serialize JSON.</summary>
    public string[] ModelPatterns { get; set; } = [];

    /// <summary>Trọng số chia tải (0–10000); lớn = nhận nhiều request hơn khi bình đẳng.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Thứ tự ưu tiên chọn (nhỏ trước, −1000–1000); độc lập với Weight.</summary>
    public int Priority { get; set; }

    /// <summary>Giới hạn token/ngày; null = không giới hạn; phải &gt; 0 nếu đặt.</summary>
    public int? DailyTokenLimit { get; set; }

    /// <summary>Giới hạn request/ngày; null = không giới hạn; phải &gt; 0 nếu đặt.</summary>
    public int? DailyRequestLimit { get; set; }
}
