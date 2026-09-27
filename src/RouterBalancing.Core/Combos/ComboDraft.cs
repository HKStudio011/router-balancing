using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Combos;

/// <summary>Bản nháp form combo — property mutable để Blazor @bind ghi được (giống ProviderDraft).</summary>
public sealed record ComboDraft
{
    public string Name { get; set; } = string.Empty;

    public ComboMode Mode { get; set; } = ComboMode.RoundRobin;

    public List<ComboItemDraft> Items { get; set; } = [];
}

/// <summary>Dòng item trong draft — đúng 1 trong 2 target, validate ở service (spec §3).</summary>
public sealed record ComboItemDraft
{
    public long? TargetModelId { get; set; }

    public long? TargetComboId { get; set; }
}
