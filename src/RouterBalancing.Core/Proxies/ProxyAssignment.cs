using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>Thông tin gán proxy của 1 provider hoặc 1 account — cho UI hiển thị.</summary>
public sealed record ProxyAssignment
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsProvider { get; init; }
    public ProxyMode? Mode { get; init; }
    public IReadOnlyList<long> ProxyIds { get; init; } = [];
}
