using System.Text.Json;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 1: GET {base}/v1/models/{id} cho provider OpenAI-compatible thông thường,
/// hoặc {base}/v1/model/{id} cho OpenRouter — thử parse field mà một số gateway
/// (OpenRouter, Together, v.v.) trả thêm. Shape "sạch" của OpenAI gốc không có
/// capabilities → trả null, nhường bước sau trong chain (Ollama/NVIDIA catalog).
/// </summary>
public sealed class ProviderEndpointMetadataProvider : IModelMetadataProvider
{
    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _protector;

    /// <inheritdoc/>
    public ProviderEndpointMetadataProvider(IHttpClientFactory http, ISecretProtector protector)
    {
        _http = http;
        _protector = protector;
    }

    /// <inheritdoc/>
    public bool ReturnsCompleteFacts => false;

    /// <inheritdoc/>
    public CapabilitySource Source => CapabilitySource.Endpoint;

    /// <inheritdoc/>
    public async Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        try
        {
            var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;
            var isOpenRouter = IsOpenRouterProvider(provider);

            // OpenRouter dùng /model số ít cho single-model lookup; các gateway khác giữ /models.
            var modelPath = isOpenRouter ? "/v1/model/" : "/v1/models/";
            using var request = ProviderRequestFactory.Create(provider, key, $"{modelPath}{model.ModelId}");
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            if (isOpenRouter
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                root = data;
            }

            var openRouterCapabilities = isOpenRouter
                ? FreeModelDetector.ParseOpenRouterCapabilities(root)
                : default;

            // Ưu tiên tường minh: context_window (OpenAI/NVIDIA) > context_length (OpenRouter) > max_model_len (vLLM)
            int? contextWindow = IntFrom(root, "context_window")
                ?? IntFrom(root, "context_length")
                ?? IntFrom(root, "max_model_len");
            // Modalities tường minh đặc hiệu hơn model_type của NVIDIA nên được ưu tiên.
            bool? vision = ParseVision(root) ?? VisionFromModelType(root);
            bool? tools = openRouterCapabilities.Tools ?? BoolFrom(root, "supports_tools");
            bool? thinking = openRouterCapabilities.Reasoning ?? BoolFrom(root, "supports_thinking");
            IReadOnlyList<string>? thinkEfforts = openRouterCapabilities.Efforts;
            IReadOnlyList<string>? input = ParseModalityList(root, "supported_modalities", "input")
                ?? ParseModalityList(root, "architecture", "input_modalities");
            IReadOnlyList<string>? output = ParseModalityList(root, "supported_modalities", "output")
                ?? ParseModalityList(root, "architecture", "output_modalities");

            // Không có field nào quen thuộc → shape lạ, nhường bước sau
            if (contextWindow is null && vision is null && tools is null && thinking is null
                && thinkEfforts is null && input is null && output is null)
            {
                return null;
            }

            return new ModelMetadata(
                contextWindow,
                vision,
                SupportsThink: thinking,
                ThinkEfforts: thinkEfforts,
                InputModalities: input,
                OutputModalities: output,
                SupportsTools: tools);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or JsonException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // Bước này best-effort — lỗi mạng/parse không được chặn chain
            return null;
        }
    }

    /// <summary>Nhận diện theo BaseUrl canonical để preset bị đổi tên vẫn dùng đúng API contract.</summary>
    private static bool IsOpenRouterProvider(Provider provider)
    {
        var entry = FreeProviderCatalog.FindByBaseUrl(provider.BaseUrl);
        return entry?.DetectKind == FreeDetectKind.PricingZeroOrFreeSuffix;
    }

    private static bool? BoolFrom(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element)
        && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static bool? VisionFromModelType(JsonElement root)
    {
        if (!root.TryGetProperty("model_type", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // Probe là nguồn positive-only: chỉ hai type VLM đã được NVIDIA định nghĩa rõ mới
        // phát biểu vision; type khác/khuyết không được suy thành KnownAbsent.
        var modelType = element.GetString();
        return string.Equals(modelType, "vlm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelType, "nv-vlm", StringComparison.OrdinalIgnoreCase)
                ? true
                : null;
    }

    private static bool? ParseVision(JsonElement root)
    {
        // Fallback kiến trúc OpenRouter: supported_modalities.input > architecture.input_modalities
        var input = ParseModalityArray(root, "supported_modalities", "input")
            ?? ParseModalityArray(root, "architecture", "input_modalities");
        if (input is null) return null;
        return input.Contains("image", StringComparer.OrdinalIgnoreCase);
    }

    private static string[]? ParseModalityList(JsonElement root, string property, string direction) =>
        ParseModalityArray(root, property, direction);

    /// <summary>Đọc mảng string 2 cấp <c>{property}.{direction}</c>; null nếu shape không khớp.</summary>
    private static string[]? ParseModalityArray(JsonElement root, string property, string direction)
    {
        if (!root.TryGetProperty(property, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(direction, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = list.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static int? IntFrom(JsonElement root, string property) =>
        root.TryGetProperty(property, out var el) && el.TryGetInt32(out var val) ? val : null;
}
