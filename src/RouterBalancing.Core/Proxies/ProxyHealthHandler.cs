using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Failover trong cùng request (spec proxy-pool §4.4): pick proxy round-robin, lỗi
/// connect-phase (HttpRequestException + inner socket/timeout/message "407") hoặc response
/// 407 → đánh down + log Warn (chỉ lần sống→down) + quay vòng proxy kế; hết proxy sống →
/// attempt direct cuối; lỗi khác → ném nguyên, không wrap.
/// </summary>
public sealed class ProxyHealthHandler : DelegatingHandler
{
    private readonly IProxyPool _pool;
    private readonly ILogService _log;

    /// <summary>Handler theo DI của HttpClientFactory — transient, InnerHandler do factory gán.</summary>
    public ProxyHealthHandler(IProxyPool pool, ILogService log)
    {
        _pool = pool;
        _log = log;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Budget = số proxy sống lúc bắt đầu request, tối thiểu 1 — tránh race budget=0
        // nhưng GetNext vẫn trả proxy (CRUD thêm giữa chừng) khiến throw null (Design decision 2)
        var budget = Math.Max(1, _pool.Snapshot().Count(s => !s.IsDown));

        // Body buffer 1 lần cho mọi attempt — content không replay được thì gửi original 1 lần
        var replayable = request.Content is null
            || request.Content is ByteArrayContent
            || request.Content is StringContent
            || request.Content is FormUrlEncodedContent;
        byte[]? body = request.Content is not null && replayable
            ? await request.Content.ReadAsByteArrayAsync(cancellationToken)
            : null;

        Exception? lastFailure = null;
        var proxyAttempts = 0;

        while (true)
        {
            var pick = _pool.GetNext();
            if (pick is null)
            {
                // Pool rỗng/tất cả down — attempt direct cuối, không report health
                // (không dính proxy nào). Direct lỗi → tự ném ra caller.
                return await SendOnceAsync(request, body, replayable, cancellationToken);
            }

            if (proxyAttempts >= budget && lastFailure is not null)
            {
                // Hết lượt retry mà vẫn còn proxy sống — ném lại lỗi cuối, không wrap (§4.4)
                ExceptionDispatchInfo.Capture(lastFailure).Throw();
            }
            proxyAttempts++;

            ProxyContext.Current = pick;
            HttpResponseMessage response;
            try
            {
                response = await SendOnceAsync(request, body, replayable, cancellationToken);
            }
            catch (Exception ex) when (IsProxyConnectFailure(ex))
            {
                lastFailure = ex;
                if (_pool.ReportFailure(pick.Id))
                {
                    LogDown(pick, ex);
                }

                if (!replayable)
                {
                    throw; // content không buffer được → không retry, ném ngay (§4.4 replay guard)
                }
                // Quay vòng — pick kế tự skip proxy vừa down; hết proxy sống → nhánh direct
                continue;
            }
            finally
            {
                // Reset trước khi rời attempt — response đã nhận xong, kết nối đã mở sẵn
                ProxyContext.Current = null;
            }

            if ((int)response.StatusCode == 407)
            {
                // Chỉ proxy sinh được 407 (auth sai/không có) — coi là proxy fail (§4.4 bước 4).
                // Xử lý SAU khối try/catch — throw trong try sẽ bị catch IsProxyConnectFailure
                // (filter match "407") của chính handler bắt lại → ReportFailure lần 2.
                response.Dispose();
                lastFailure = new HttpRequestException(
                    "The proxy server returned HTTP 407 (Proxy Authentication Required).");
                if (_pool.ReportFailure(pick.Id))
                {
                    LogDown(pick, lastFailure);
                }
                if (!replayable)
                {
                    // Content không replay được nên không thể attempt tiếp — ném 407 ngay
                    // thay vì gửi lại cùng HttpRequestMessage (vi phạm Design decision 3).
                    // !replayable luôn là attempt đầu (các đường khác đã thoát sớm) nên
                    // lastFailure luôn là exception 407 vừa tạo.
                    throw lastFailure;
                }
                continue;
            }

            // Có response từ upstream (kể cả 5xx — 502 sinh tại proxy vẫn tính proxy sống)
            _pool.ReportSuccess(pick.Id);
            return response;
        }
    }

    /// <summary>Gửi 1 attempt — không replay được thì gửi original, còn lại clone từ body đã buffer.</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request, byte[]? body, bool replayable, CancellationToken ct)
    {
        if (!replayable)
        {
            return await base.SendAsync(request, ct);
        }

        // Clone từng attempt: không gửi lại cùng HttpRequestMessage (Design decision 3)
        using var clone = CloneRequest(request, body);
        return await base.SendAsync(clone, ct);
    }

    /// <summary>Copy method/uri/headers/version + body buffer — request mới cho từng attempt.</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    /// <summary>Lỗi connect-phase tới proxy — chỉ những kiểu này mới failover (spec §4.4).</summary>
    private static bool IsProxyConnectFailure(Exception ex) =>
        ex is HttpRequestException && (
            ex.InnerException is SocketException
            || ex.InnerException is TimeoutException
            || ex.Message.Contains("407", StringComparison.Ordinal));

    private void LogDown(ProxyAttempt pick, Exception ex) =>
        // Chỉ khi ReportFailure trả true (sống→down) — không lặp mỗi request (spec §7);
        // message KHÔNG chứa credentials, chỉ endpoint + nội dung lỗi
        _log.Warn($"Proxy {pick.Endpoint} đánh dấu down 60s sau lỗi kết nối: {ex.Message}");
}
