using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Echo client cho test tay: HttpClient + WebProxy **cố định** cho đúng proxy cần test,
/// Credentials tường minh (không qua <see cref="ProxyContext"/>, không qua
/// <see cref="ProxyHealthHandler"/> — test tay không đụng health pool, spec §4.5).
/// Kết nối 10s, timeout tổng 10s.
/// </summary>
public sealed class ProxyEchoClient : IProxyEchoClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Uri _echoUrl;

    // Đo elapsed bằng Stopwatch trực tiếp — không inject TimeProvider để không lẫn
    // 2 cách đo trong cùng codebase (DI Task 8 đăng ký khớp ctor này).
    /// <param name="echoUrl">Endpoint echo — test truyền destination của LocalHttpServer.</param>
    public ProxyEchoClient(string echoUrl = "https://api.ipify.org/?format=json")
    {
        _echoUrl = new Uri(echoUrl);
    }

    /// <inheritdoc/>
    public async Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.Uri)
            {
                Credentials = proxy.Username is null
                    ? null
                    : new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty),
            },
            UseProxy = true,
            ConnectTimeout = Timeout,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };

        var started = Stopwatch.GetTimestamp();
        using var response = await http.GetAsync(_echoUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        response.EnsureSuccessStatusCode(); // non-2xx → HttpRequestException, để ProxyService persist

        // IP egress best-effort — echo URL dạng {"ip":"..."}; body khác (stub trả {"ok":true})
        // hay JSON hỏng → Ip = null, test vẫn pass (không fail chỉ vì thiếu IP)
        string? ip = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("ip", out var ipElement))
            {
                ip = ipElement.GetString();
            }
        }
        catch (JsonException)
        {
            // body không phải JSON {"ip"} — chấp nhận, Ip null
        }

        return new ProxyTestResult(true, null, (int)response.StatusCode, elapsed, ip);
    }
}
