namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bản nháp form account. Property mutable (không phải positional record)
/// để Blazor <c>@bind</c> ghi được — giống <c>ProviderDraft</c>.
/// </summary>
public sealed class ProviderAccountDraft
{
    public long ProviderId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Plaintext lúc nhập; rỗng khi update = giữ key đã lưu. Không bao giờ log/persist trực tiếp.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Pattern do UI gửi (1 phần tử = 1 dòng textarea); service serialize JSON.</summary>
    public string[] ModelPatterns { get; set; } = [];

    public int Weight { get; set; } = 100;

    public int Priority { get; set; }

    public int? DailyTokenLimit { get; set; }

    public int? DailyRequestLimit { get; set; }
}
