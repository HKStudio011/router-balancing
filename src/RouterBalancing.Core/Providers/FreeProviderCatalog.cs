namespace RouterBalancing.Core.Providers;

/// <summary>Cách nhận diện model free trong response list của từng provider (spec provider-free §6.3).</summary>
public enum FreeDetectKind
{
    /// <summary>pricing parse về decimal == 0 ("0", "0.0") HOẶC id endswith ":free" (OpenRouter).</summary>
    PricingZeroOrFreeSuffix,

    /// <summary>id endswith "-free" (OpenCode Zen — response không có pricing).</summary>
    FreeSuffix,

    /// <summary>Mọi model trong response đều free (NVIDIA NIM, Ollama Cloud — không có field pricing).</summary>
    AllFree,

    /// <summary>
    /// Detect theo catalog riêng <c>models.opencode.ai/api.json</c> (spec opencode-tools-page §6.2) —
    /// response KHÔNG theo OpenAI shape nên đi nhánh parse <c>FreeModelDetector.ParseOpenCodeCatalog</c>.
    /// </summary>
    OpenCodeCatalog,
}

/// <summary>
/// Catalog 4 provider free preset — hardcode trong code theo spec §2 (không config ngoài file).
/// BaseUrl canonical KHÔNG kèm /v1: ProviderRequestFactory ghép "/v1/models" +
/// ProviderUrl.Canonicalize chống "/v1/v1" (spec §4 — bảng probe thật 2026-10-01).
/// </summary>
public static class FreeProviderCatalog
{
    /// <summary>1 preset free: tên hiển thị (đồng thời là khóa match), gốc URL, cách detect free.</summary>
    /// <param name="CatalogUrl">Nguồn catalog riêng (absolute URL) khi <paramref name="DetectKind"/> là
    /// <see cref="FreeDetectKind.OpenCodeCatalog"/>; null cho các preset dùng models endpoint thường (spec §6.1).</param>
    /// <param name="CompleteCapabilityList">true khi danh mục của provider liệt kê ĐẦY ĐỦ capability
    /// cho mỗi model — sync được phép sinh dòng KnownAbsent (C-P, spec capability-model §5 luật (b));
    /// false khi list không có thông tin capability (NVIDIA — Task 7 lo phần đó). Cờ này chỉ dùng
    /// cho đường sync list; đường fill dùng <see cref="IModelMetadataProvider.ReturnsCompleteFacts"/> (R-T7-2).</param>
    public sealed record Entry(
        string DisplayName,
        string BaseUrl,
        FreeDetectKind DetectKind,
        string? CatalogUrl = null,
        bool CompleteCapabilityList = false);

    /// <summary>4 preset seed lúc khởi động — thứ tự = thứ tự insert (spec §4).</summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("OpenCode Free", "https://opencode.ai/zen", FreeDetectKind.OpenCodeCatalog, "https://models.opencode.ai/api.json", CompleteCapabilityList: true),
        new("OpenRouter Free", "https://openrouter.ai/api", FreeDetectKind.PricingZeroOrFreeSuffix, CompleteCapabilityList: true),
        new("NVIDIA NIM Free", "https://integrate.api.nvidia.com", FreeDetectKind.AllFree),
        new("Ollama Cloud Free", "https://ollama.com", FreeDetectKind.AllFree, CompleteCapabilityList: true),
    ];

    /// <summary>
    /// Tra catalog theo Name — user đổi tên preset thì mất link sync (chốt S1, spec §6.2.2);
    /// service ném FreeModelSyncException, periodic log warning bỏ qua.
    /// </summary>
    /// <returns><see langword="null"/> nếu không match — user đã đổi tên preset.</returns>
    public static Entry? FindByDisplayName(string displayName) =>
        Entries.FirstOrDefault(e => string.Equals(e.DisplayName, displayName, StringComparison.Ordinal));

    /// <summary>
    /// Tra catalog theo BaseUrl canonical (rename-safe cho trang Tools, spec opencode-tools-page C4/C7):
    /// user đổi tên preset vẫn nhận diện được preset free.
    /// </summary>
    /// <returns><see langword="null"/> nếu BaseUrl không khớp preset free nào.</returns>
    public static Entry? FindByBaseUrl(string baseUrl)
    {
        var canonical = ProviderUrl.Canonicalize(baseUrl);
        return Entries.FirstOrDefault(e =>
            string.Equals(e.BaseUrl, canonical, StringComparison.OrdinalIgnoreCase));
    }
}
