using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 1: GET {base}/v1/models/{id} — thử parse field mà một số gateway
/// (OpenRouter, Together, v.v.) trả thêm. Shape "sạch" của OpenAI gốc không có
/// capabilities → trả null, nhường static catalog.
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
    public async Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        try
        {
            var key = string.IsNullOrEmpty(provider.ApiKeyEncrypted)
                ? string.Empty
                : _protector.Unprotect(provider.ApiKeyEncrypted);

            using var request = ProviderRequestFactory.Create(provider, key, $"/v1/models/{model.ModelId}");
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;

            // Ưu tiên tường minh: context_window (OpenAI) > context_length (OpenRouter) > max_model_len (vLLM)
            int? contextWindow = IntFrom(root, "context_window")
                ?? IntFrom(root, "context_length")
                ?? IntFrom(root, "max_model_len");
            bool? vision = ParseVision(root);
            string? input = ParseModalityList(root, "supported_modalities", "input")
                ?? ParseModalityList(root, "architecture", "input_modalities");
            string? output = ParseModalityList(root, "supported_modalities", "output")
                ?? ParseModalityList(root, "architecture", "output_modalities");

            // Không có field nào quen thuộc → shape lạ, nhường bước sau
            if (contextWindow is null && vision is null && input is null && output is null)
            {
                return null;
            }

            return new ModelMetadata(
                contextWindow,
                vision,
                SupportsThink: null,      // endpoint ít khi trả — catalog lo phần này
                ThinkEfforts: null,
                InputModalities: input,
                OutputModalities: output);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or JsonException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // Bước này best-effort — lỗi mạng/parse không được chặn chain
            return null;
        }
    }

    private static bool? ParseVision(JsonElement root)
    {
        // Fallback kiến trúc OpenRouter: supported_modalities.input > architecture.input_modalities
        var input = ParseModalityArray(root, "supported_modalities", "input")
            ?? ParseModalityArray(root, "architecture", "input_modalities");
        if (input is null) return null;
        return input.Contains("image", StringComparer.OrdinalIgnoreCase);
    }

    private static string? ParseModalityList(JsonElement root, string property, string direction)
    {
        var values = ParseModalityArray(root, property, direction);
        return values is null ? null : JsonSerializer.Serialize(values);
    }

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
