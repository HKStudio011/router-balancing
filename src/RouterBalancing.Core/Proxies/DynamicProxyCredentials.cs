using System.Net;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Credentials động theo request: .NET 10 KHÔNG parse userinfo trong proxy URI
/// (dotnet/runtime#125341, fix vào .NET 11) nên auth phải đi qua
/// <see cref="IWebProxy.Credentials"/>. Phục vụ cả HTTP 407-challenge lẫn SOCKS5 handshake
/// — verify bằng stub tests (spec §4.6, risk §11.4).
/// </summary>
public sealed class DynamicProxyCredentials : ICredentials
{
    /// <summary>
    /// 1 instance ổn định — <c>SocketsHttpHandler</c> đọc <c>Proxy.Credentials</c> đúng 1 lần
    /// lúc construct, getter không được evaluate pick tại đó.
    /// </summary>
    public static DynamicProxyCredentials Instance { get; } = new();

    private DynamicProxyCredentials()
    {
    }

    /// <inheritdoc/>
    public NetworkCredential? GetCredential(Uri? uri, string authType) =>
        ProxyContext.Current is { Username: { Length: > 0 } username } attempt
            ? new NetworkCredential(username, attempt.Password ?? string.Empty)
            : null;
}
