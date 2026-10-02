using System.Net;
using System.Net.Sockets;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

public class ProxyHealthHandlerTests
{
    private static ProxyAttempt Attempt(long id, int port) =>
        new(id, new Uri($"http://127.0.0.1:{port}"), null, null, $"http://127.0.0.1:{port}");

    private static HttpRequestException ConnectFailure() =>
        new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task SendAsync_ConnectFailure_ReportsAndFailsOverToNextProxy()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Equal([2L], pool.Successes);
        // Attempt 1 qua p1, attempt 2 qua p2 — context ghi đúng pick của từng call
        Assert.Equal(1L, stub.ContextAtCall[0]!.Id);
        Assert.Equal(2L, stub.ContextAtCall[1]!.Id);
        Assert.Null(ProxyContext.Current); // reset sau khi handler xong
    }

    [Fact]
    public async Task SendAsync_Upstream500_ReportsSuccessNotFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(pool.Failures); // 5xx từ upstream ≠ proxy fail (§4.4)
        Assert.Equal([1L], pool.Successes);
    }

    [Fact]
    public async Task SendAsync_407Response_TreatedAsProxyFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1L], pool.Failures); // 407 = proxy fail, failover sang p2
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: HttpRequestException inner TimeoutException → proxy fail, failover
    // (đủ chỉ inner timeout, không cần message "407").
    public async Task SendAsync_TimeoutInnerHttpRequestException_Failover()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? throw new HttpRequestException("timeout", new TimeoutException())
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures); // p1 timeout inner → đánh down, p2 thành công
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: message chứa "407" (không inner) → proxy fail, failover
    // (đủ chỉ message "407", không cần inner socket/timeout).
    public async Task SendAsync_MessageContains407_Failover()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? throw new HttpRequestException("HTTP 407 Proxy Authentication Required")
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures); // p1 message "407" → đánh down, p2 thành công
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: HttpRequestException không inner socket/timeout, không "407"
    // → KHÔNG phải proxy fail: ném nguyên ra caller, không đánh down (Failures rỗng).
    public async Task SendAsync_NonProxyHttpRequestException_NoReportFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => throw new HttpRequestException("boom"));
        using var client = ClientFor(pool, stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://upstream.example/v1/chat"));

        Assert.Equal(1, stub.Calls); // không failover — p2 không được gọi
        Assert.Empty(pool.Failures); // không phải proxy fail → không report
        Assert.Empty(pool.Successes);
    }

    [Fact]
    public async Task SendAsync_AllProxiesFail_FallsBackToDirect()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Null(stub.ContextAtCall[1]); // direct attempt — không dính proxy nào
    }

    [Fact]
    public async Task SendAsync_BudgetExhausted_ThrowsLastFailureWithoutRetry()
    {
        // FakePool không gỡ proxy khỏi list → GetNext luôn trả → pin nhánh hết budget
        var pool = new FakePool(removeOnFailure: false).Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://upstream.example/v1/chat"));

        Assert.Equal(2, stub.Calls); // 2 attempt theo budget, không có attempt thứ 3
        Assert.Null(ProxyContext.Current);
    }

    [Fact]
    public async Task SendAsync_NonReplayableContent_DoesNotRetry()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream.example/v1/chat")
        {
            Content = new OpaqueContent(),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, stub.Calls); // không retry — ném ngay (§4.4 replay guard)
        Assert.Equal([1L], pool.Failures);
    }

    [Fact]
    public async Task SendAsync_NonReplayableContent_Throws407WithoutRetry()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));
        using var client = ClientFor(pool, stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream.example/v1/chat")
        {
            Content = new OpaqueContent(),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Contains("407", ex.Message); // ném 407 ngay, không failover
        Assert.Equal(1, stub.Calls); // không gửi lần 2 — replay guard mirror nhánh exception
        Assert.Equal([1L], pool.Failures); // ReportFailure đúng 1 lần cho proxy 1
    }

    [Fact]
    public async Task SendAsync_EmptyPool_AttemptsDirectImmediately()
    {
        var pool = new FakePool();
        var stub = new StubHandler(_ => Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(stub.ContextAtCall[0]); // không scope → IsBypassed → direct
        Assert.Empty(pool.Failures);
    }

    private static HttpClient ClientFor(FakePool pool, StubHandler stub) =>
        new(new ProxyHealthHandler(pool, new NullLog()) { InnerHandler = stub });

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    /// <summary>
    /// Pool scriptable: ghi lại report thành công/thất bại; <c>removeOnFailure=false</c> giữ proxy trong list
    /// để test nhánh hết budget (GetNext vẫn trả proxy sau khi đã fail).
    /// </summary>
    private sealed class FakePool(bool removeOnFailure = true) : IProxyPool
    {
        private readonly List<ProxyAttempt> _alive = [];
        private int _cursor;

        public List<long> Failures { get; } = [];

        public List<long> Successes { get; } = [];

        public FakePool Add(params ProxyAttempt[] attempts)
        {
            _alive.AddRange(attempts);
            return this;
        }

        public ProxyAttempt? GetNext()
        {
            if (_alive.Count == 0)
            {
                return null;
            }

            var pick = _alive[_cursor % _alive.Count];
            _cursor++;
            return pick;
        }

        public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)
        {
            var candidates = allowedIds is not null
                ? _alive.Where(a => allowedIds.Contains(a.Id)).ToList()
                : _alive;
            if (candidates.Count == 0)
            {
                return null;
            }

            var pick = candidates[_cursor % candidates.Count];
            _cursor++;
            return pick;
        }

        public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids) =>
            _alive.Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).ToList();

        public bool ReportFailure(long proxyId)
        {
            Failures.Add(proxyId);
            return removeOnFailure ? _alive.RemoveAll(a => a.Id == proxyId) > 0 : true;
        }

        public void ReportSuccess(long proxyId) => Successes.Add(proxyId);

        public void Invalidate()
        {
        }

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() =>
            _alive.Select(a => new ProxyRuntimeStatus(a.Id, a.Endpoint, false, null)).ToList();
    }

    /// <summary>Inner handler ghi lại call count + ProxyContext tại thời điểm gọi — script có thể ném.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<ProxyAttempt?> ContextAtCall { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ContextAtCall.Add(ProxyContext.Current);
            return Task.FromResult(script(Calls));
        }
    }

    /// <summary>Content không thuộc nhóm replayable (không kế thừa ByteArrayContent).</summary>
    private sealed class OpaqueContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("x"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
