using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RouterBalancing.Core.Server;

/// <summary>Danh sách URL IPv4 non-loopback của máy — hiển thị khi bật LAN (D-C3).</summary>
public static class LanUrlProvider
{
    /// <summary>
    /// Mỗi NIC đang Up (không loopback) một URL <c>http://ip:port</c>; port chưa xác định
    /// (≤ 0) hoặc không có NIC phù hợp → danh sách rỗng — caller tự render/tự bỏ qua.
    /// </summary>
    public static IReadOnlyList<string> GetUrls(int port)
    {
        if (port <= 0)
            return [];
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(a.Address))
            .Select(a => $"http://{a.Address}:{port}")
            .Distinct()
            .ToList();
    }
}
