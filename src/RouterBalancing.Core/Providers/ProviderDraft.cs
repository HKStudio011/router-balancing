using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bản nháp form provider. Property mutable (không phải positional record)
/// để Blazor <c>@bind</c> ghi được — giống <c>SettingsDraft</c>.
/// </summary>
public sealed record ProviderDraft
{
    public string Name { get; set; } = string.Empty;

    public ProviderType Type { get; set; } = ProviderType.OpenAI;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Plaintext từ form — chỉ tồn tại trong lúc nhập, không bao giờ log hay persist trực tiếp.
    /// Chỉ có nghĩa khi <c>CreateAsync</c> (tạo account "Default"); <c>UpdateAsync</c> bỏ qua.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public int MaxConcurrent { get; set; } = 4;
}
