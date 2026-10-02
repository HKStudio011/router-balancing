using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// HTTP destination server trên 127.0.0.1 (port random): nhận GET, trả JSON
/// <c>{"ok":true}</c>, luôn đóng kết nối sau response (<c>Connection: close</c>) —
/// stub forward bằng read-to-EOF được, deterministic (spec §4.6).
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _requestsHandled;

    public int Port { get; }

    /// <summary>Số request đã nhận — assert direct/proxy tới destination.</summary>
    public int RequestsHandled => Volatile.Read(ref _requestsHandled);

    public LocalHttpServer()
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
                return; // listener đóng lúc Dispose — không phải lỗi hệ thống
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            await TcpHeadReader.ReadHeadAsync(stream);
            Interlocked.Increment(ref _requestsHandled);

            var body = "{\"ok\":true}"u8.ToArray();
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
    }

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
            // chấp nhận được khi dispose giữa chừng — không nuốt lỗi khác
        }

        _cts.Dispose();
    }
}

/// <summary>Đọc HTTP head (headers đến dòng trống) byte-by-byte — tránh cần parser đầy đủ.</summary>
internal static class TcpHeadReader
{
    public static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var ms = new MemoryStream();
        var buffer = new byte[1];
        while (ms.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            ms.WriteByte(buffer[0]);
            if (ms.Length >= 4)
            {
                var bytes = ms.GetBuffer();
                if (bytes[ms.Length - 4] == (byte)'\r'
                    && bytes[ms.Length - 3] == (byte)'\n'
                    && bytes[ms.Length - 2] == (byte)'\r'
                    && bytes[ms.Length - 1] == (byte)'\n')
                {
                    break;
                }
            }
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
