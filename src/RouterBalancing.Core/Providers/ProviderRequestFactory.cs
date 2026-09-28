using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Tạo request GET tới provider — dùng chung cho test connection, fetch models
/// và metadata endpoint để header theo Type chỉ viết 1 chỗ (DRY).
/// </summary>
public static class ProviderRequestFactory
{
    /// <summary>Tên named HttpClient — timeout 10s, đăng ký trong MauiProgram.</summary>
    public const string HttpClientName = "provider-probe";

    /// <summary>
    /// Tạo request tới provider. Header theo Type:
    /// OpenAI → <c>Authorization: Bearer</c>; Anthropic → <c>x-api-key</c> + <c>anthropic-version</c>.
    /// </summary>
    /// <param name="provider">Provider đích.</param>
    /// <param name="apiKey">Key plaintext sẽ gắn Authorization/x-api-key.</param>
    /// <param name="path">Path mặc định <c>/v1/models</c>.</param>
    /// <param name="method">HTTP method — mặc định GET (probe); POST cho chat completion.</param>
    /// <param name="content">Body request — chỉ dùng khi có <paramref name="method"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Khi Type ngoài 2 giá trị đã biết.</exception>
    public static HttpRequestMessage Create(Provider provider, string apiKey, string? path = null,
        HttpMethod? method = null, HttpContent? content = null)
    {
        // Canonicalize ngay lúc ghép (idempotent): fix runtime cho row lưu trước khi có save-fix
        // — chỉ khi path tự có /v1, không đoán với path không version
        var requestPath = path ?? "/v1/models";
        var baseUrl = requestPath.StartsWith("/v1", StringComparison.Ordinal)
            ? ProviderUrl.Canonicalize(provider.BaseUrl)
            : provider.BaseUrl.TrimEnd('/');
        var url = baseUrl + requestPath;
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url) { Content = content };

        switch (provider.Type)
        {
            case ProviderType.OpenAI:
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                break;
            case ProviderType.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider.Type), provider.Type, "Unsupported provider type.");
        }

        return request;
    }
}
