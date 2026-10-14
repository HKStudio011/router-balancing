namespace RouterBalancing.Core.Combos;

/// <summary>Capabilities của combo — toàn bộ nullable; null = chưa rõ (spec D2).</summary>
public sealed record ComboCapabilities(
    int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking);

/// <summary>Tính capabilities cho combo từ member (model/combo con) — AND 3 mức, ctx = min (spec D2).</summary>
public interface IComboCapabilityCalculator
{
    /// <summary>Tính capabilities cho mọi combo trong DB — gọi 1 lần, memoize theo combo Id.</summary>
    Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default);
}
