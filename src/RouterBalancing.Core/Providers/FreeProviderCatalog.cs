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
}

/// <summary>
/// Catalog 4 provider free preset — hardcode trong code theo spec §2 (không config ngoài file).
/// BaseUrl canonical KHÔNG kèm /v1: ProviderRequestFactory ghép "/v1/models" +
/// ProviderUrl.Canonicalize chống "/v1/v1" (spec §4 — bảng probe thật 2026-10-01).
/// </summary>
public static class FreeProviderCatalog
{
    /// <summary>1 preset free: tên hiển thị (đồng thời là khóa match), gốc URL, cách detect free.</summary>
    public sealed record Entry(string DisplayName, string BaseUrl, FreeDetectKind DetectKind);

    /// <summary>4 preset seed lúc khởi động — thứ tự = thứ tự insert (spec §4).</summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("OpenCode Free", "https://opencode.ai/zen", FreeDetectKind.FreeSuffix),
        new("OpenRouter Free", "https://openrouter.ai/api", FreeDetectKind.PricingZeroOrFreeSuffix),
        new("NVIDIA NIM Free", "https://integrate.api.nvidia.com", FreeDetectKind.AllFree),
        new("Ollama Cloud Free", "https://ollama.com", FreeDetectKind.AllFree),
    ];

    /// <summary>
    /// Tra catalog theo Name — user đổi tên preset thì mất link sync (chốt S1, spec §6.2.2);
    /// service ném FreeModelSyncException, periodic log warning bỏ qua.
    /// </summary>
    /// <returns><see langword="null"/> nếu không match — user đã đổi tên preset.</returns>
    public static Entry? FindByDisplayName(string displayName) =>
        Entries.FirstOrDefault(e => string.Equals(e.DisplayName, displayName, StringComparison.Ordinal));
}
