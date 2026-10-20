namespace RouterBalancing.Core.Combos;

/// <summary>Capabilities của combo — toàn bộ nullable; null = chưa rõ (spec D2).</summary>
public sealed record ComboCapabilities(
    int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking)
{
    /// <summary>
    /// Model gọi tool được — AND 3 mức như các bool khác; null = chưa rõ (C-L: giao an toàn).
    /// Init-only để positional construction cũ vẫn hợp lệ.
    /// </summary>
    public bool? Tools { get; init; }

    /// <summary>
    /// Giao các modality input mà MỌI member khẳng định thuận; null = có member chưa rõ
    /// (không được suy "chắc chắn không có" từ im lặng — C-I/C-L).
    /// </summary>
    public IReadOnlyList<string>? InputModalities { get; init; }

    /// <inheritdoc cref="InputModalities"/>
    public IReadOnlyList<string>? OutputModalities { get; init; }
}

/// <summary>Tính capabilities cho combo từ member (model/combo con) — AND 3 mức, ctx = min (spec D2).</summary>
public interface IComboCapabilityCalculator
{
    /// <summary>Tính capabilities cho mọi combo trong DB — gọi 1 lần, memoize theo combo Id.</summary>
    Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default);
}
