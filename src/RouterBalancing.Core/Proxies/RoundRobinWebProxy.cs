using System.Net;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// <see cref="IWebProxy"/> đọc <see cref="ProxyContext"/> — proxy nào dùng cho request do
/// <see cref="ProxyHealthHandler"/> quyết định; không có scope = bypass (direct) (spec §4.3).
/// Kết nối tới cùng proxy tái sử dụng nhờ connection grouping per-proxy có sẵn của SocketsHttpHandler.
/// </summary>
public sealed class RoundRobinWebProxy : IWebProxy
{
    /// <inheritdoc/>
    public ICredentials? Credentials
    {
        // IWebProxy.Credentials bắt buộc setter nhưng intent là luôn trả instance ổn định
        // (SocketsHttpHandler đọc 1 lần lúc construct) — set là no-op, không thay thế được.
        get => DynamicProxyCredentials.Instance;
        set
        {
        }
    }

    /// <inheritdoc/>
    public Uri? GetProxy(Uri destination) => ProxyContext.Current?.Uri;

    /// <inheritdoc/>
    public bool IsBypassed(Uri host) => ProxyContext.Current is null;
}
