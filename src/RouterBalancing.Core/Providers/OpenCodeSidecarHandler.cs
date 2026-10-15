using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RouterBalancing.Core.Proxies;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// DelegatingHandler chèn vào cuối upstream pipeline: request POST chat tới
/// <c>opencode.ai</c> được reroute sang Go sidecar (TLS crypto/tls vượt gate
/// 403 FreeTierError — Schannel của .NET bị chặn JA3/JA4). Request khác đi
/// thẳng inner handler. Proxy scope đang phục vụ được truyền cho sidecar qua
/// <c>X-Proxy-Url</c> để sidecar dial outbound qua đúng proxy (credential đã
/// URL-encode) — failover/health-check vẫn do <c>ProxyHealthHandler</c> điều phối.
/// </summary>
public sealed class OpenCodeSidecarHandler : DelegatingHandler
{
    private const string SidecarPath = "/v1/chat/completions";
    private readonly IOpenCodeSidecarService _sidecar;

    /// <summary>
    /// Tạo handler reroute opencode.ai sang sidecar.
    /// </summary>
    /// <param name="sidecar">Service quản lý Go sidecar process.</param>
    /// <param name="innerHandler">Handler kế tiếp trong pipeline (SocketsHttpHandler).</param>
    public OpenCodeSidecarHandler(IOpenCodeSidecarService sidecar, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _sidecar = sidecar;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsOpenCodeChat(request))
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        if (!await _sidecar.EnsureStartedAsync(ct).ConfigureAwait(false) || _sidecar.BaseUrl is null)
        {
            throw ProxyConnectFailure("OpenCode sidecar unavailable (binary missing or failed to start).");
        }

        var sidecarRequest = await CloneForSidecarAsync(request, _sidecar.BaseUrl, ct).ConfigureAwait(false);
        using var response = await base.SendAsync(sidecarRequest, ct).ConfigureAwait(false);

        // 502 + X-Proxy-Failure: sidecar không dial được outbound proxy — ném
        // cùng shape lỗi proxy-connect để ProxyHealthHandler attempt proxy khác.
        if (response.StatusCode == HttpStatusCode.BadGateway
            && response.Headers.Contains("X-Proxy-Failure"))
        {
            response.Dispose();
            throw ProxyConnectFailure("OpenCode sidecar failed to connect via proxy.");
        }

        return response;
    }

    private static bool IsOpenCodeChat(HttpRequestMessage request) =>
        request.RequestUri is { Host: "opencode.ai" } uri
        && uri.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpRequestMessage> CloneForSidecarAsync(
        HttpRequestMessage request, string sidecarBaseUrl, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(HttpMethod.Post, sidecarBaseUrl.TrimEnd('/') + SidecarPath);

        if (request.Content is not null)
        {
            clone.Content = new StreamContent(await request.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Sidecar chỉ nhận request loopback — không forward scheme/host cũ.
        clone.Headers.Host = null;

        if (ProxyContext.Current is { } attempt)
        {
            clone.Headers.TryAddWithoutValidation("X-Proxy-Url", FormatProxyUrl(attempt));
        }

        return clone;
    }

    /// <summary>
    /// Ghép X-Proxy-Url dạng <c>scheme://user:pass@host:port</c> — userinfo phải
    /// EscapeDataString vì password chứa ký tự đặc biệt (<c>@</c>, <c>:</c>, <c>#</c>)
    /// sẽ phá parse URL phía Go (<c>url.Parse</c>).
    /// </summary>
    private static string FormatProxyUrl(ProxyAttempt attempt)
    {
        var builder = new UriBuilder(attempt.Uri);
        if (attempt.Username is not null)
        {
            builder.UserName = Uri.EscapeDataString(attempt.Username);
            builder.Password = Uri.EscapeDataString(attempt.Password ?? string.Empty);
        }
        // UriBuilder luôn thêm trailing slash (socks5://host:port/) — trim cho gọn;
        // Go url.Parse không phân biệt, nhưng header test so sánh exact string.
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static HttpRequestException ProxyConnectFailure(string message) =>
        // Inner SocketException: ProxyHealthHandler.IsProxyConnectFailure chỉ
        // match HttpRequestException có inner SocketException/message proxy —
        // đúng shape đó thì failover sang proxy khác (hoặc direct) được kích hoạt.
        new(message, new SocketException((int)SocketError.ConnectionRefused));
}
