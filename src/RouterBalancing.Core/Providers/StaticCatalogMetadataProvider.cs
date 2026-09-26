using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 2: catalog metadata nhúng cho model phổ biến — best-effort, prefix-match.
/// Giá trị là ước lượng công khai tại thời điểm viết; sai số chấp nhận được vì
/// user vẫn thấy badge "manual-edit phase sau" — không chặn functionality.
/// </summary>
public sealed class StaticCatalogMetadataProvider : IModelMetadataProvider
{
    private sealed record Entry(string Prefix, ModelMetadata Meta);

    // Thứ tự quan trọng: prefix dài/đặc thù trước prefix chung chung
    private static readonly Entry[] Catalog =
    [
        new("gpt-4o-mini", new(128_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("gpt-4o",     new(128_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("gpt-4.1",    new(1_047_576, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("o1",         new(200_000, SupportsVision: true, SupportsThink: true, """["low","medium","high"]""", """["text","image"]""", """["text"]""")),
        new("o3",         new(200_000, SupportsVision: true, SupportsThink: true, """["low","medium","high"]""", """["text","image"]""", """["text"]""")),
        new("claude-3-7-sonnet", new(200_000, SupportsVision: true, SupportsThink: true, """["low","high"]""", """["text","image"]""", """["text"]""")),
        new("claude-3-5",  new(200_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("claude-4",    new(200_000, SupportsVision: true, SupportsThink: true, """["low","high"]""", """["text","image"]""", """["text"]""")),
        new("deepseek-reasoner", new(64_000, SupportsVision: false, SupportsThink: true, null, """["text"]""", """["text"]""")),
        new("deepseek-chat",     new(64_000, SupportsVision: false, SupportsThink: false, null, """["text"]""", """["text"]""")),
    ];

    /// <inheritdoc/>
    public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        // OrdinalIgnoreCase: id model thường lowercase nhưng không bắt buộc
        var entry = Catalog.FirstOrDefault(e =>
            model.ModelId.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(entry?.Meta);
    }
}
