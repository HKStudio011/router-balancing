namespace RouterBalancing.Core.Proxies;

/// <summary>Gửi 1 request echo qua proxy cụ thể để test tay — tách interface để unit test mock (spec §4.5).</summary>
public interface IProxyEchoClient
{
    /// <summary>Gửi request echo qua <paramref name="proxy"/> (credentials đã decrypt).</summary>
    /// <param name="proxy">Proxy cần test.</param>
    /// <param name="ct">Token hủy của caller — hủy thật phải ném <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="HttpRequestException">Không kết nối qua được proxy hoặc upstream non-2xx.</exception>
    /// <exception cref="TaskCanceledException">Timeout 10s của HttpClient (không do <paramref name="ct"/>).</exception>
    Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct);
}
