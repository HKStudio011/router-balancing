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

            int? contextWindow = root.TryGetProperty("context_window", out var ctx) && ctx.TryGetInt32(out var ctxVal)
                ? ctxVal
                : null;
            bool? vision = ParseVision(root);
            string? input = ParseModalityList(root, "supported_modalities", "input");
            string? output = ParseModalityList(root, "supported_modalities", "output");

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
        if (!root.TryGetProperty("supported_modalities", out var mods)
            || mods.ValueKind != JsonValueKind.Object
            || !mods.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in input.EnumerateArray())
        {
            if (string.Equals(item.GetString(), "image", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ParseModalityList(JsonElement root, string property, string direction)
    {
        if (!root.TryGetProperty(property, out var mods)
            || mods.ValueKind != JsonValueKind.Object
            || !mods.TryGetProperty(direction, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = list.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();
        return values.Length == 0 ? null : JsonSerializer.Serialize(values);
    }
}
