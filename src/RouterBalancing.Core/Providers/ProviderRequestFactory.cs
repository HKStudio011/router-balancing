using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Tạo request tới provider — GET mặc định với path <c>/v1/models</c> (test connection,
/// fetch models, metadata); POST khi truyền method/content (chat completion). Header theo
/// Type chỉ viết 1 chỗ (DRY).
/// </summary>
public static class ProviderRequestFactory
{
    /// <summary>Tên named HttpClient — timeout 10s, đăng ký trong MauiProgram.</summary>
    public const string HttpClientName = "provider-probe";

    /// <summary>
    /// Tạo request tới provider. Header theo Type:
    /// OpenAI → <c>Authorization: Bearer</c>; Anthropic → <c>x-api-key</c> + <c>anthropic-version</c>
    /// (key rỗng = bỏ header auth — no-key, vẫn giữ anthropic-version).
    /// </summary>
    /// <param name="provider">Provider đích.</param>
    /// <param name="apiKey">Key plaintext sẽ gắn Authorization/x-api-key; rỗng = không gắn header auth.</param>
    /// <param name="path">Path mặc định <c>/v1/models</c>.</param>
    /// <param name="method">HTTP method — mặc định GET (probe); POST cho chat completion.</param>
    /// <param name="content">Body request — chỉ dùng khi có <paramref name="method"/>.</param>
    /// <param name="idempotencyKey">
    /// Gắn header <c>Idempotency-Key</c> (chỉ khi khác rỗng) — request id gốc giữ nguyên qua
    /// mọi retry/failover để provider dedupe request bị gửi lại. Chỉ POST chat dùng;
    /// GET probe truyền null (không idempotency cần dedupe).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Khi Type ngoài 2 giá trị đã biết.</exception>
    public static HttpRequestMessage Create(Provider provider, string apiKey, string? path = null,
        HttpMethod? method = null, HttpContent? content = null, string? idempotencyKey = null)
    {
        // Canonicalize ngay lúc ghép (idempotent): fix runtime cho row lưu trước khi có save-fix
        // — chỉ khi path tự có /v1, không đoán với path không version
        var requestPath = path ?? "/v1/models";
        var baseUrl = requestPath.StartsWith("/v1", StringComparison.Ordinal)
            ? ProviderUrl.Canonicalize(provider.BaseUrl)
            : provider.BaseUrl.TrimEnd('/');
        var url = baseUrl + requestPath;
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url) { Content = content };

        // Idempotency-Key TRƯỚC ApplyExtraHeaders — extra headers chỉ thêm key chưa tồn tại
        // nên key của request (dedupe) không bị user override nhầm
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        switch (provider.Type)
        {
            case ProviderType.OpenAI:
                // Key rỗng = account no-key → bỏ hẳn Authorization (D7) — "Bearer" trần làm
                // nhiều upstream trả 401 oan dù server không yêu cầu key
                if (apiKey.Length > 0)
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                break;
            case ProviderType.Anthropic:
                if (apiKey.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider.Type), provider.Type, "Unsupported provider type.");
        }

        // OpenCode: gắn fingerprint (UA/x-opencode-*/Accept + transform body quartet tools)
        // — thiếu → 403 FreeTierError "within OpenCode", áp cho cả free và keyed lane.
        // Chỉ POST có content (chat); GET probe đang chạy tốt nên không đụng tới.
        if (content is not null && OpenCodeFreeFingerprint.IsApplicable(provider))
        {
            OpenCodeFreeFingerprint.Apply(request, provider, apiKey, content);
        }

        // Attribution headers (HTTP-Referer/X-Title cho NIM + OpenRouter; billing origin
        // chỉ NIM) — gắn theo provider kể cả GET probe, như opencode/hermes
        if (ProviderAttribution.IsApplicable(provider))
        {
            ProviderAttribution.Apply(request, provider);
        }

        // NVIDIA NIM: bỏ name/tool_name khỏi role=tool (NIM ToolMessage schema hẹp hơn)
        if (NvidiaRequestProfile.IsApplicable(provider))
        {
            NvidiaRequestProfile.Apply(request, content);
        }

        // Header tùy chỉnh người dùng (options.headers kiểu opencode.json) — áp cho mọi
        // request kể cả GET probe. Fingerprint/attributions đã set ở trên thắng:
        // chỉ bổ sung key chưa tồn tại, không đè Authorization/x-api-key/UA.
        ApplyExtraHeaders(request, provider);

        return request;
    }

    /// <summary>
    /// Parse JSON object trong <see cref="Provider.ExtraHeaders"/> và gắn từng entry
    /// (TryAddWithoutValidation) khi header tương ứng chưa có. JSON hỏng (chỉnh tay DB)
    /// → bỏ qua, không chặn traffic.
    /// </summary>
    private static void ApplyExtraHeaders(HttpRequestMessage request, Provider provider)
    {
        if (provider.ExtraHeaders is not { Length: > 0 })
        {
            return;
        }

        Dictionary<string, string> headers;
        try
        {
            headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                provider.ExtraHeaders) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            if (!request.Headers.Contains(key))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
