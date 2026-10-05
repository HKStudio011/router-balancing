using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Attribution headers cho provider cần ghi nhận app trên dashboard của upstream —
/// opencode và hermes đều gắn <c>HTTP-Referer</c>/<c>X-Title</c> khi gọi các host này:
/// NVIDIA NIM (thêm <c>X-BILLING-INVOKE-ORIGIN</c> — thiếu thì traffic bị unattributed
/// trên billing dashboard) và OpenRouter (chỉ 2 header kia — OpenRouter không dùng
/// billing origin, dùng Referer/Title cho app rankings).
/// </summary>
public static class ProviderAttribution
{
    private const string NvidiaHost = "integrate.api.nvidia.com";
    private const string OpenRouterHost = "openrouter.ai";

    private const string Title = "RouterBalancing";
    private const string Referer = "https://github.com/HKStudio011/router-balancing";
    private const string NvidiaBillingOrigin = "RouterBalancing";

    /// <summary>
    /// Chỉ áp cho provider OpenAI-type trỏ đúng host NIM/OpenRouter — host so exact,
    /// suffix lừa (vd <c>openrouter.ai.evil.com</c>) không match nên không bị gắn oan.
    /// </summary>
    /// <param name="provider">Provider đích.</param>
    /// <returns><see langword="true"/> khi host thuộc danh sách cần attribution.</returns>
    public static bool IsApplicable(Provider provider) => TargetHost(provider) is not null;

    /// <summary>
    /// Gắn <c>HTTP-Referer</c>/<c>X-Title</c> (mọi target);
    /// <c>X-BILLING-INVOKE-ORIGIN</c> chỉ với host NVIDIA.
    /// </summary>
    /// <param name="request">Request đã có URI — gắn header vào đây.</param>
    /// <param name="provider">Provider đích để phân biệt target.</param>
    public static void Apply(HttpRequestMessage request, Provider provider)
    {
        request.Headers.TryAddWithoutValidation("HTTP-Referer", Referer);
        request.Headers.TryAddWithoutValidation("X-Title", Title);

        if (string.Equals(TargetHost(provider), NvidiaHost, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("X-BILLING-INVOKE-ORIGIN", NvidiaBillingOrigin);
        }
    }

    /// <returns>Host của provider nếu cần attribution; <see langword="null"/> nếu không.</returns>
    private static string? TargetHost(Provider provider)
    {
        if (provider.Type != ProviderType.OpenAI) return null;
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        return string.Equals(host, NvidiaHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, OpenRouterHost, StringComparison.OrdinalIgnoreCase)
            ? host
            : null;
    }
}
