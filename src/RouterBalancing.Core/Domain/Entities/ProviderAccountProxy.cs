namespace RouterBalancing.Core.Domain;

/// <summary>Junction ProviderAccount ↔ OutboundProxy — gán proxy cho account (override provider).</summary>
public class ProviderAccountProxy
{
    public long AccountId { get; set; }
    public long ProxyId { get; set; }

    public ProviderAccount? Account { get; set; } = null!;
    public OutboundProxy? Proxy { get; set; } = null!;
}
