using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>Một scope (provider/account) đang dùng một proxy — data source cho reverse view (D-A6).</summary>
/// <param name="ScopeId">Id của provider hoặc account.</param>
/// <param name="ScopeName">Tên hiển thị của scope.</param>
/// <param name="IsProvider">true = provider, false = account.</param>
/// <param name="Mode">Chế độ dùng proxy của scope đó (null = kế thừa/unified).</param>
public sealed record ProxyUsage(long ScopeId, string ScopeName, bool IsProvider, ProxyMode? Mode);
