using System.Net;
using System.Net.Sockets;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class PortSelectorTests
{
    [Fact]
    public void FindAvailable_WhenPreferredFree_ReturnsPreferred()
    {
        var free = PortSelector.FindAvailable(20000);

        Assert.Equal(20000, free);
    }

    [Fact]
    public void FindAvailable_WhenPreferredOccupied_ReturnsOtherPort()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var result = PortSelector.FindAvailable(occupied);

        Assert.NotEqual(occupied, result);
        Assert.InRange(result, 1024, 65535);
    }

    [Fact]
    public void FindAvailable_WhenAllAttemptsFail_ThrowsIOException()
    {
        // Khóa kín một dải port liên tục rồi yêu cầu tìm trong đúng dải đó
        var listeners = new List<TcpListener>();
        var start = PortSelector.FindAvailable(21000);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, start + i);
                l.Start();
                listeners.Add(l);
            }

            Assert.Throws<IOException>(() => PortSelector.FindAvailable(start, maxAttempts: 5));
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_OnFreePort_ReturnsTrue()
    {
        var port = PortSelector.FindAvailable(22000);

        Assert.True(PortSelector.IsPortAvailable(port));
    }
}
