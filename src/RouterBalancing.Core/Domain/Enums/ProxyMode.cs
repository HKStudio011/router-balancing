namespace RouterBalancing.Core.Domain;

/// <summary>
/// Cách dùng tập proxy đã gán cho provider/account (spec proxy-per-provider §2):
/// <c>RoundRobin</c> = RR + cooldown; <c>Fallback</c> = thử theo ProxyId tăng, fail → kế.
/// </summary>
public enum ProxyMode
{
    RoundRobin = 0,
    Fallback = 1,
}
