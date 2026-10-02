namespace RouterBalancing.Core.Domain;

/// <summary>Junction Provider ↔ OutboundProxy — gán proxy cho provider (M2M).</summary>
public class ProviderProxy
{
    public long ProviderId { get; set; }
    public long ProxyId { get; set; }

    public Provider? Provider { get; set; } = null!;
    public OutboundProxy? Proxy { get; set; } = null!;
}
