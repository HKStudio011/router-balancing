using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// <see cref="OpenCodeFreeFingerprint"/> sau v2 (decision A) chỉ còn LANE SELECTION cho
/// <c>opencode.ai</c> — shaping (UA/x-opencode-*, đổi tên tool, restore tên trong SSE) đã
/// chuyển hẳn sang Go sidecar (<c>sidecar/opencode-proxy</c>), hợp đồng đó test phía Go.
/// Ở đây giữ hợp đồng nhận diện lane — đầu vào cho phân loại gate + lane dataset.
/// </summary>
public class OpenCodeFreeFingerprintTests
{
    private static Provider ProviderOf(ProviderType type, string baseUrl) => new()
    {
        Name = "P",
        Type = type,
        BaseUrl = baseUrl,
    };

    [Theory]
    [InlineData("https://opencode.ai/zen")]      // BaseUrl thật của lane free
    [InlineData("https://opencode.ai")]          // không path
    [InlineData("HTTPS://OPENCODE.AI/zen")]      // host so sánh ignore-case
    public void IsApplicable_OpenCodeHost_ReturnsTrue(string baseUrl) =>
        Assert.True(OpenCodeFreeFingerprint.IsApplicable(
            ProviderOf(ProviderType.OpenAI, baseUrl)));

    [Theory]
    [InlineData("https://api.openai.com")]        // provider thường — không qua sidecar
    [InlineData("https://opencode.ai.evil.com")]  // host chỉ trùng tiền tố (không phải subdomain thật)
    [InlineData("not-a-url")]                     // không parse được URI tuyệt đối → ngoài lane
    [InlineData("")]                              // rỗng
    public void IsApplicable_NonOpenCodeHost_ReturnsFalse(string baseUrl) =>
        Assert.False(OpenCodeFreeFingerprint.IsApplicable(
            ProviderOf(ProviderType.OpenAI, baseUrl)));

    [Fact]
    public void IsApplicable_AnthropicTypeOnOpenCodeHost_ReturnsFalse()
    {
        // Lane chỉ định nghĩa cho Type OpenAI (đường chat reroute sang sidecar) — host trùng
        // nhưng Type Anthropic không đi qua handler/shape của lane
        Assert.False(OpenCodeFreeFingerprint.IsApplicable(
            ProviderOf(ProviderType.Anthropic, "https://opencode.ai/zen")));
    }
}
