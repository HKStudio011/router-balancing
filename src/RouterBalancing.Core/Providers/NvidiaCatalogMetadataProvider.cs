using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Nguồn chính thức của NVIDIA API Catalog: trang model <c>https://build.nvidia.com/{publisher}/{slug}</c>
/// render server-side khối <c>&lt;aside aria-label="Model information"&gt;</c>. Sidebar chỉ có
/// <c>Context Length</c>/<c>Input Modalities</c>/<c>Output Modalities</c> — không có
/// Function Calling/Structured Output/Reasoning — nên provider chỉ phát biểu modality (và
/// context qua cột scalar), KHÔNG bịa tools/reasoning (R-T7-2).
/// 66/81 model NVIDIA trong DB có trang 200 nhưng không có sidebar → trả <see langword="null"/>.
/// Kết quả trang được cache theo model id TTL 24h (kể cả 200-không-sidebar) để lần fill sau
/// không tải lại 81 trang; lỗi HTTP/mạng không cache.
/// </summary>
public sealed class NvidiaCatalogMetadataProvider : IModelMetadataProvider
{
    private const string NvidiaBaseUrl = "https://integrate.api.nvidia.com";
    private const string CatalogBaseUrl = "https://build.nvidia.com";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly Regex AsideStartPattern = new(
        """<aside\b[^>]*\baria-label\s*=\s*["']Model information["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex DataPairPattern = new(
        @"<dt\b[^>]*>(?<term>.*?)</dt>\s*<dd\b[^>]*>(?<value>.*?)</dd>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex TagPattern = new(
        "<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant);

    private static readonly Regex ContextLengthPattern = new(
        @"^(?<number>\d+(?:\.\d+)?)\s*(?<suffix>[KkMm])?$",
        RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<FetchResult>>> _inFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Tạo provider. <paramref name="timeProvider"/> mặc định <see cref="TimeProvider.System"/>;
    /// DI truyền singleton đã đăng ký để test điều khiển TTL.
    /// </summary>
    public NvidiaCatalogMetadataProvider(
        IHttpClientFactory http, ILogService log, TimeProvider? timeProvider = null)
    {
        _http = http;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public bool ReturnsCompleteFacts => true;

    /// <inheritdoc/>
    public CapabilitySource Source => CapabilitySource.Catalog;

    /// <inheritdoc/>
    public async Task<ModelMetadata?> FetchAsync(
        Provider provider, Model model, CancellationToken ct = default)
    {
        if (!IsNvidiaProvider(provider)) return null;

        var modelId = model.ModelId.Trim();
        if (modelId.Length == 0) return null;

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(modelId, out var cached) && now - cached.FetchedAt < CacheTtl)
        {
            return cached.Metadata;
        }

        // Single-flight theo từng model: request đầu tiên tạo Lazy, các caller trùng id
        // chờ chung task; model khác id có Lazy riêng nên vẫn chạy song song (Task 8).
        var lazy = _inFlight.GetOrAdd(
            modelId,
            _ => new Lazy<Task<FetchResult>>(
                () => FetchAndCacheAsync(modelId, now, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var result = await lazy.Value.ConfigureAwait(false);
        return result.Metadata;
    }

    /// <summary>Nhận diện theo BaseUrl canonical qua <see cref="FreeProviderCatalog"/> (rename-safe).</summary>
    private static bool IsNvidiaProvider(Provider provider)
    {
        var entry = FreeProviderCatalog.FindByBaseUrl(provider.BaseUrl);
        return string.Equals(entry?.BaseUrl, NvidiaBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FetchResult> FetchAndCacheAsync(
        string modelId, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        try
        {
            var result = await FetchPageAsync(modelId, ct).ConfigureAwait(false);
            // 200 là "nguồn đã trả lời" — cache cả trường hợp không có sidebar (negative cache).
            // Non-200/network error không cache: lần fill sau còn cơ hội thử lại.
            if (result.HttpOk)
            {
                _cache[modelId] = new CacheEntry(fetchedAt, result.Metadata);
            }
            return result;
        }
        finally
        {
            // Chỉ chính task này sở hữu entry (GetOrAdd không thể thay entry đang có),
            // nên remove ở đây không đụng Lazy của caller khác.
            _inFlight.TryRemove(modelId, out _);
        }
    }

    private async Task<FetchResult> FetchPageAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var uri = BuildPageUri(modelId);
            if (uri is null) return new FetchResult(false, null);

            // Nguồn công khai: absolute URI + named client (proxy pipeline), không auth header.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return new FetchResult(false, null);

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new FetchResult(true, ParsePage(html, modelId));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or IOException or InvalidOperationException
                                       or RegexMatchTimeoutException or UriFormatException)
        {
            // Best-effort như mọi bước metadata: lỗi mạng/trang trả null, không ném.
            _log.Warn($"NVIDIA catalog metadata lỗi cho model {modelId}: {ex.Message}");
            return new FetchResult(false, null);
        }
    }

    private static Uri? BuildPageUri(string modelId)
    {
        var segments = modelId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var path = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri($"{CatalogBaseUrl}/{path}", UriKind.Absolute);
    }

    /// <summary>
    /// Parse sidebar đã xác nhận có mặt. Trả metadata kể cả khi thiếu từng field — sidebar
    /// có mặt nghĩa là nguồn chính thức đã trả lời; field vắng chỉ là nguồn không nói.
    /// </summary>
    private ModelMetadata? ParsePage(string html, string modelId)
    {
        var asideStart = AsideStartPattern.Match(html);
        if (!asideStart.Success) return null;

        var asideEnd = html.IndexOf(
            "</aside>", asideStart.Index + asideStart.Length, StringComparison.OrdinalIgnoreCase);
        if (asideEnd < 0) return null;

        var block = html[asideStart.Index..(asideEnd + "</aside>".Length)];
        var fields = ParsePairs(block);

        int? contextWindow = fields.TryGetValue("Context Length", out var rawContext)
            ? ParseContextLength(rawContext)
            : null;
        var input = fields.TryGetValue("Input Modalities", out var rawInput)
            ? ParseModalities(rawInput)
            : null;
        var output = fields.TryGetValue("Output Modalities", out var rawOutput)
            ? ParseModalities(rawOutput)
            : null;
        LogUnknownModalityTokens(modelId, input, output);

        // Sidebar không có field tools/reasoning/vision — để null để mapper không sinh
        // phát biểu nào cho các dimension đó (R-T7-2).
        return new ModelMetadata(
            ContextWindow: contextWindow,
            SupportsVision: null,
            SupportsThink: null,
            ThinkEfforts: null,
            InputModalities: input,
            OutputModalities: output,
            SupportsTools: null);
    }

    private static Dictionary<string, string> ParsePairs(string block)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DataPairPattern.Matches(block))
        {
            pairs[NormalizeText(match.Groups["term"].Value)] =
                NormalizeText(match.Groups["value"].Value);
        }
        return pairs;
    }

    /// <summary>Strip tag → HTML decode → gộp whitespace; text sidebar nằm trong nested span/svg.</summary>
    private static string NormalizeText(string html)
    {
        var withoutTags = TagPattern.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespacePattern.Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// Chuyển <c>262K</c>/<c>1M</c>/số thuần sang token. Dùng decimal để không làm tròn
    /// floating-point; kết quả lẻ token hoặc vượt <see cref="int.MaxValue"/> là dữ liệu hỏng → null.
    /// </summary>
    private static int? ParseContextLength(string raw)
    {
        var match = ContextLengthPattern.Match(raw);
        if (!match.Success) return null;
        if (!decimal.TryParse(
                match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        var multiplier = match.Groups["suffix"].Value.ToUpperInvariant() switch
        {
            "K" => 1_000m,
            "M" => 1_000_000m,
            _ => 1m,
        };
        if (value <= 0 || value > int.MaxValue / multiplier) return null;

        var tokens = value * multiplier;
        if (tokens != decimal.Truncate(tokens)) return null;
        return (int)tokens;
    }

    /// <summary>
    /// Tách chuỗi <c>"Text, Image, Video"</c> thành token đã trim; mapping sang
    /// <see cref="ModalityKind"/> nằm ở <see cref="MetadataPatchMapper"/> (không đoán token).
    /// </summary>
    private static IReadOnlyList<string>? ParseModalities(string raw)
    {
        var tokens = raw.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? null : tokens;
    }

    private void LogUnknownModalityTokens(
        string modelId, IReadOnlyList<string>? input, IReadOnlyList<string>? output)
    {
        var unknown = (input ?? []).Concat(output ?? [])
            .Where(token => !CapabilityNames.TryFromDb(token.ToLowerInvariant(), out ModalityKind _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length == 0) return;

        // Một dòng/model/lần fetch — không ném; token vẫn được giữ trong metadata để mapper
        // bỏ đúng chỗ (R-T5-5).
        _log.Warn($"NVIDIA catalog: model {modelId} có modality token lạ " +
                  $"[{string.Join(", ", unknown)}] — bỏ qua.");
    }

    private readonly record struct CacheEntry(DateTimeOffset FetchedAt, ModelMetadata? Metadata);

    private readonly record struct FetchResult(bool HttpOk, ModelMetadata? Metadata);
}
