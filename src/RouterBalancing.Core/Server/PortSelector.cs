using System.Net;
using System.Net.Sockets;

namespace RouterBalancing.Core.Server;

/// <summary>Tìm port trống trên loopback — tránh crash khi port mặc định đã bị chiếm.</summary>
public static class PortSelector
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    /// <summary>Tìm port khả dụng bắt đầu từ <paramref name="preferred"/>, tăng dần tối đa <paramref name="maxAttempts"/> lần.</summary>
    /// <exception cref="IOException">Khi không còn port trống trong dải đã thử.</exception>
    public static int FindAvailable(int preferred = 8317, int maxAttempts = 50)
    {
        var start = Math.Clamp(preferred, MinPort, MaxPort);
        for (var port = start; port <= Math.Min(start + maxAttempts - 1, MaxPort); port++)
        {
            if (IsPortAvailable(port)) return port;
        }
        throw new IOException($"Không tìm thấy port trống sau {maxAttempts} lần thử (bắt đầu từ {start}).");
    }

    /// <summary>Thử bind thật — cổng đã lắng nghe ở bất kỳ địa chỉ nào trả về false.</summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
