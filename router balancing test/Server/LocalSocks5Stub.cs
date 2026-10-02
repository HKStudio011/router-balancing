using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// SOCKS5 stub (RFC1928, optional username/password RFC1929): greeting → auth (nếu bật)
/// → CONNECT (ATYP 01/03/04) → tunnel raw TCP tới target (spec §4.6, risk §11.4/11.5).
/// Ghi lại username đã handshake để test xác minh credentials đi qua AsyncLocal.
/// </summary>
public sealed class LocalSocks5Stub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _usernames = [];
    private int _tunnelsEstablished;
    private int _authFailures;

    public int Port { get; }

    /// <summary>Yêu cầu RFC1929 username/password — không set thì method no-auth.</summary>
    public string? RequireUser { get; init; }

    public string? RequirePassword { get; init; }

    public bool RequireAuth => RequireUser is not null;

    public int TunnelsEstablished => Volatile.Read(ref _tunnelsEstablished);

    public int AuthFailures => Volatile.Read(ref _authFailures);

    public IReadOnlyList<string> Usernames
    {
        get
        {
            lock (_usernames)
            {
                return _usernames.ToArray();
            }
        }
    }

    public LocalSocks5Stub()
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
            try
            {
                var stream = client.GetStream();

                // ===== Greeting: 05 <nmethods> <methods> =====
                var greeting = await ReadExactAsync(stream, 2);
                var methods = await ReadExactAsync(stream, greeting[1]);
                if (RequireAuth)
                {
                    // Cast byte: nếu truyền 0x02 (int), inference fixes TSource=int →
                    // Enumerable.Contains<int> không nhận byte[] (CS1929)
                    if (!methods.Contains((byte)0x02))
                    {
                        await stream.WriteAsync(new byte[] { 0x05, 0xFF });
                        Interlocked.Increment(ref _authFailures);
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 0x05, 0x02 });

                    // ===== RFC1929: 01 <ulen> <user> <plen> <pass> =====
                    var verUlen = await ReadExactAsync(stream, 2);
                    var user = Encoding.UTF8.GetString(await ReadExactAsync(stream, verUlen[1]));
                    var plen = (await ReadExactAsync(stream, 1))[0];
                    var pass = Encoding.UTF8.GetString(await ReadExactAsync(stream, plen));
                    lock (_usernames)
                    {
                        _usernames.Add(user);
                    }

                    if (user != RequireUser || pass != RequirePassword)
                    {
                        await stream.WriteAsync(new byte[] { 0x01, 0x01 });
                        Interlocked.Increment(ref _authFailures);
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 0x01, 0x00 });
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                }

                // ===== CONNECT: 05 01 00 <atyp> <addr> <port> =====
                var request = await ReadExactAsync(stream, 4);
                if (request[1] != 0x01)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                (var host, var port) = await ReadAddressAsync(stream, request[3]);
                using var forward = new TcpClient();
                try
                {
                    await forward.ConnectAsync(host, port);
                }
                catch (SocketException)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                // Bind addr 0.0.0.0:0 — client không quan tâm giá trị bind
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                Interlocked.Increment(ref _tunnelsEstablished);

                // ===== Pipe 2 chiều: client ↔ target (Connection: close từ destination → EOF) =====
                var forwardStream = forward.GetStream();
                var targetToClient = forwardStream.CopyToAsync(stream);
                var clientToTarget = stream.CopyToAsync(forwardStream);
                await Task.WhenAny(targetToClient, clientToTarget);
            }
            catch (IOException)
            {
                // client đóng giữa handshake — bình thường khi test dispose/dọn dẹp
            }
            catch (ObjectDisposedException)
            {
                // stream đóng khi dispose — bình thường
            }
        }
    }

    private static async Task<(string host, int port)> ReadAddressAsync(NetworkStream stream, byte atyp)
    {
        string host;
        if (atyp == 0x01)
        {
            var addr = await ReadExactAsync(stream, 4);
            host = $"{addr[0]}.{addr[1]}.{addr[2]}.{addr[3]}";
        }
        else if (atyp == 0x03)
        {
            var length = (await ReadExactAsync(stream, 1))[0];
            host = Encoding.ASCII.GetString(await ReadExactAsync(stream, length));
        }
        else if (atyp == 0x04)
        {
            var addr = await ReadExactAsync(stream, 16);
            host = new IPAddress(addr).ToString();
        }
        else
        {
            throw new IOException($"SOCKS5 atyp không hỗ trợ: 0x{atyp:X2}");
        }

        var portBytes = await ReadExactAsync(stream, 2);
        return (host, (portBytes[0] << 8) | portBytes[1]);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new IOException("Client đóng kết nối giữa handshake SOCKS5.");
            }

            offset += read;
        }

        return buffer;
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
            // chấp nhận được khi dispose giữa chừng
        }

        _cts.Dispose();
    }
}
