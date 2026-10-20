using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Nguồn capability chính thức của Ollama Cloud: <c>GET https://ollama.com/api/tags</c>
/// (không auth) trả <c>capabilities</c> cho từng model. Đây là full-list cho ĐÚNG ba
/// dimension tools/thinking/vision (R-T6-1: từ vựng Ollama không có audio/video/text nên
/// patch không được phát biểu về chúng — im lặng ở đó là "nguồn không nói", không phải
/// "không hỗ trợ"). Tags cache in-process TTL 24h (R-T6-3) vì fill gọi provider theo
/// từng model.
/// </summary>
public sealed class OllamaTagsMetadataProvider : IModelMetadataProvider
{
    private const string OllamaBaseUrl = "https://ollama.com";
    private static readonly Uri TagsUri = new($"{OllamaBaseUrl}/api/tags");
    private static readonly TimeSpan TagsCacheTtl = TimeSpan.FromHours(24);
    private static readonly string[] KnownCapabilities = ["completion", "tools", "thinking", "vision"];

    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private TagsCacheEntry? _cache;

    /// <summary>
    /// Tạo provider. <paramref name="timeProvider"/> mặc định <see cref="TimeProvider.System"/>;
    /// DI truyền singleton đã đăng ký để Task 8 dùng chung clock.
    /// </summary>
    public OllamaTagsMetadataProvider(IHttpClientFactory http, ILogService log, TimeProvider? timeProvider = null)
    {
        _http = http;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public bool ReturnsCompleteFacts => true;

    /// <inheritdoc/>
    public async Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        if (!IsOllamaProvider(provider)) return null;

        var tags = await GetTagsAsync(ct);
        if (tags is null) return null;

        var entry = FindEntry(tags, model.ModelId);
        return entry is null ? null : MapEntry(entry, model.ModelId);
    }

    /// <summary>Nhận diện theo BaseUrl canonical qua <see cref="FreeProviderCatalog"/> (rename-safe).</summary>
    private static bool IsOllamaProvider(Provider provider)
    {
        var entry = FreeProviderCatalog.FindByBaseUrl(provider.BaseUrl);
        return string.Equals(entry?.BaseUrl, OllamaBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cache lấy sau khóa; chỉ response thành công mới được ghi (R-T6-3).</summary>
    private async Task<IReadOnlyList<TagsModel>?> GetTagsAsync(CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_cache is { } cached && now - cached.FetchedAt < TagsCacheTtl)
            {
                return cached.Models;
            }

            var fetched = await FetchTagsAsync(ct);
            if (fetched is not null)
            {
                _cache = new TagsCacheEntry(now, fetched);
            }
            return fetched;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<IReadOnlyList<TagsModel>?> FetchTagsAsync(CancellationToken ct)
    {
        try
        {
            // Nguồn công khai: absolute URI + named client (proxy pipeline), không auth header.
            using var request = new HttpRequestMessage(HttpMethod.Get, TagsUri);
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return ParseModels(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or JsonException or InvalidOperationException)
        {
            // Best-effort như mọi bước metadata: lỗi mạng/parse trả null, không ném.
            _log.Warn($"Ollama tags metadata lỗi: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<TagsModel>? ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<TagsModel>();
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = StringOrNull(item, "name");
            var model = StringOrNull(item, "model");
            if (name is null && model is null) continue;

            // Vắng property hoặc JSON null = không phát biểu (R-T5-7); mảng rỗng = phát biểu "không có gì".
            IReadOnlyList<string>? capabilities = null;
            if (item.TryGetProperty("capabilities", out var capabilityList)
                && capabilityList.ValueKind == JsonValueKind.Array)
            {
                capabilities = capabilityList.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .ToArray();
            }

            entries.Add(new TagsModel(name, model, capabilities));
        }
        return entries;
    }

    private static string? StringOrNull(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Khớp exact <c>name</c> trước, rồi <c>model</c>; chỉ khi cả hai miss mới bỏ suffix <c>:tag</c>.</summary>
    private static TagsModel? FindEntry(IReadOnlyList<TagsModel> tags, string modelId)
    {
        var exact = FindByExactId(tags, modelId);
        if (exact is not null) return exact;

        var colon = modelId.LastIndexOf(':');
        if (colon <= 0 || colon == modelId.Length - 1) return null;
        return FindByExactId(tags, modelId[..colon]);
    }

    private static TagsModel? FindByExactId(IReadOnlyList<TagsModel> tags, string id) =>
        tags.FirstOrDefault(t => string.Equals(t.Name, id, StringComparison.Ordinal))
        ?? tags.FirstOrDefault(t => string.Equals(t.Model, id, StringComparison.Ordinal));

    private ModelMetadata MapEntry(TagsModel entry, string modelId)
    {
        if (entry.Capabilities is null)
        {
            // R-T5-7: vắng mảng capabilities = nguồn không phát biểu, KHÔNG phải toàn false.
            return new ModelMetadata(null, null, null, null, null, null);
        }

        var capabilities = new HashSet<string>(entry.Capabilities, StringComparer.OrdinalIgnoreCase);
        var unknown = entry.Capabilities
            .Where(token => !KnownCapabilities.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (unknown.Length > 0)
        {
            // Đúng một dòng/model/lần gọi (không phải một dòng/token — pin); không ném.
            _log.Warn($"Ollama tags: model {modelId} có capability token lạ [{string.Join(", ", unknown)}] — bỏ qua.");
        }

        return new ModelMetadata(
            ContextWindow: null,
            SupportsVision: capabilities.Contains("vision"),
            SupportsThink: capabilities.Contains("thinking"),
            ThinkEfforts: null,
            InputModalities: null, // R-T6-1: không phát biểu text/audio/video — từ vựng Ollama không có
            OutputModalities: null,
            SupportsTools: capabilities.Contains("tools"));
    }

    private sealed record TagsModel(string? Name, string? Model, IReadOnlyList<string>? Capabilities);

    private readonly record struct TagsCacheEntry(DateTimeOffset FetchedAt, IReadOnlyList<TagsModel> Models);
}
