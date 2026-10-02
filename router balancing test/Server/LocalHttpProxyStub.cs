using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// HTTP forward proxy stub (chỉ GET, tuyệt đối-URI, không CONNECT — spec §4.6):
/// ghi lại request line + Proxy-Authorization, optional bắt Basic auth (sai/thiếu → 407),
/// đúng thì forward tới target (origin-form, <c>Connection: close</c>) và pipe response về.
/// </summary>
public sealed class LocalHttpProxyStub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _requestLines = [];
    private readonly List<string?> _proxyAuthorizationHeaders = [];
    private int _requestsHandled;

    public int Port { get; }

    /// <summary>Yêu cầu Basic auth với credential này — không set thì bỏ qua auth.</summary>
    public string? RequireUser { get; init; }

    public string? RequirePassword { get; init; }

    public bool RequireAuth => RequireUser is not null;

    /// <summary>Số request đã forward (không tính request bị 407).</summary>
    public int RequestsHandled => Volatile.Read(ref _requestsHandled);

    public IReadOnlyList<string> RequestLines
    {
        get
        {
            lock (_requestLines)
            {
                return _requestLines.ToArray();
            }
        }
    }

    public IReadOnlyList<string?> ProxyAuthorizationHeaders
    {
        get
        {
            lock (_proxyAuthorizationHeaders)
            {
                return _proxyAuthorizationHeaders.ToArray();
            }
        }
    }

    public LocalHttpProxyStub()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener đóng lúc Dispose
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var head = await TcpHeadReader.ReadHeadAsync(stream);
            var lines = head.Split("\r\n");

            var requestLine = lines.Length > 0 ? lines[0] : string.Empty;
            var authHeader = GetHeader(lines, "proxy-authorization");
            lock (_requestLines)
            {
                _requestLines.Add(requestLine);
                _proxyAuthorizationHeaders.Add(authHeader);
            }

            if (RequireAuth && authHeader != ExpectedBasicHeader())
            {
                // Tách riêng byte[] — "str" + "str"u8.ToArray() bind thành string.Concat rồi lỗi biên dịch
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n"
                    + "Proxy-Authenticate: Basic realm=\"stub\"\r\n"
                    + "Content-Length: 0\r\nConnection: close\r\n\r\n"));
                return;
            }

            await ForwardAsync(stream, requestLine, lines);
        }
    }

    // Không static: tăng _requestsHandled (instance field) khi forward thành công
    private async Task ForwardAsync(NetworkStream clientStream, string requestLine, string[] lines)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 3 || parts[0] != "GET")
        {
            // Stub chỉ phục vụ GET absolute-URI — method khác là bug của caller test
            await clientStream.WriteAsync(
                "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return;
        }

        var target = new Uri(parts[1]);
        using var forward = new TcpClient();
        await forward.ConnectAsync(target.Host, target.Port);
        var forwardStream = forward.GetStream();

        // Rewrite absolute-URI → origin-form, bỏ header riêng của proxy, ép close để read-to-EOF
        var sb = new StringBuilder();
        sb.Append(parts[0]).Append(' ').Append(target.PathAndQuery).Append(" HTTP/1.1\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0
                || line.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append(line).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");
        await forwardStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));

        Interlocked.Increment(ref _requestsHandled);
        await forwardStream.CopyToAsync(clientStream); // EOF khi destination đóng → đóng client
    }

    private string ExpectedBasicHeader() =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RequireUser}:{RequirePassword}"));

    private static string? GetHeader(string[] lines, string name) =>
        lines.Skip(1)
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?[(name.Length + 1)..].Trim();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or SocketException or ObjectDisposedException))
        {
            // chấp nhận được khi dispose giữa chừng
        }

        _cts.Dispose();
    }
}
