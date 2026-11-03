using System.Net.Sockets;

namespace router_balancing_test;

/// <summary>
/// Endpoint mạng cố định dùng chung cho test proxy failover.
/// </summary>
internal static class TestNet
{
    private const int FirstClosedPort = 1;
    private const int LastClosedPort = 8;

    /// <summary>
    /// Cổng loopback "chết" thứ <paramref name="index"/> (0-based) để test "proxy chết" —
    /// không listener, connect bị refused tức thì và KHÔNG race với test song song.
    /// Trước đây mỗi file tự "reserve ephemeral port rồi release": port vừa nhả có thể bị
    /// test khác bind lại ngay (Windows tái dùng port vừa free) → connect "đến proxy chết"
    /// đôi khi thành công rồi treo tới timeout (flake 28/10: ProxyHealthHandlerTests 4.5s).
    /// Port 1/2/3… nằm ngoài dải ephemeral và cần quyền admin mới bind được → luôn đóng;
    /// index khác nhau cho các test cần nhiều proxy chết (DB unique theo Scheme+Host+Port).
    /// </summary>
    public static int ClosedPort(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (FirstClosedPort + index > LastClosedPort)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"Chỉ dành riêng port {FirstClosedPort}..{LastClosedPort} cho proxy chết.");
        }

        return FirstClosedPort + index;
    }

    /// <summary>
    /// Port nằm trong dải dành riêng "luôn đóng". Test dùng để biến connect tới proxy chết
    /// thành failure tức thì qua <c>SocketsHttpHandler.ConnectCallback</c>: connect thật tới
    /// port đóng có thể chờ hết <c>ConnectTimeout</c> dưới tải song song (flake 2026-11-03).
    /// </summary>
    public static bool IsReservedClosedPort(int port) =>
        port >= FirstClosedPort && port <= LastClosedPort;

    /// <summary>
    /// Kết nối TCP thật — thay cho connect mặc định của <c>SocketsHttpHandler</c> khi test
    /// cài <c>ConnectCallback</c> để chặn riêng dải port chết.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
