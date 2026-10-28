namespace router_balancing_test;

/// <summary>
/// Endpoint mạng cố định dùng chung cho test proxy failover.
/// </summary>
internal static class TestNet
{
    /// <summary>
    /// Cổng loopback "chết" thứ <paramref name="index"/> (0-based) để test "proxy chết" —
    /// không listener, connect bị refused tức thì và KHÔNG race với test song song.
    /// Trước đây mỗi file tự "reserve ephemeral port rồi release": port vừa nhả có thể bị
    /// test khác bind lại ngay (Windows tái dùng port vừa free) → connect "đến proxy chết"
    /// đôi khi thành công rồi treo tới timeout (flake 28/10: ProxyHealthHandlerTests 4.5s).
    /// Port 1/2/3… nằm ngoài dải ephemeral và cần quyền admin mới bind được → luôn đóng;
    /// index khác nhau cho các test cần nhiều proxy chết (DB unique theo Scheme+Host+Port).
    /// </summary>
    public static int ClosedPort(int index) => 1 + index;
}
