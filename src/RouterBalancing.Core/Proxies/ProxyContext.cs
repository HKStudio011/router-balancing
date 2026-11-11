namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Correlation giữa pick của <see cref="ProxyHealthHandler"/> và
/// <see cref="RoundRobinWebProxy.GetProxy"/> — <c>IWebProxy.GetProxy(Uri)</c> chỉ nhận
/// destination nên dùng <see cref="AsyncLocal{T}"/> để biết request nào đang gọi (spec §4.2).
/// Không có scope (không qua handler) → bypass (direct) — deterministic, không RR ngầm.
/// </summary>
public static class ProxyContext
{
    private static readonly AsyncLocal<ProxyAttempt?> s_current = new();
    private static readonly AsyncLocal<PickHolder?> s_lastPick = new();

    /// <summary>Proxy đang phục vụ request hiện tại — <see langword="null"/> = direct.</summary>
    public static ProxyAttempt? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>
    /// Exit proxy của attempt GẦN NHẤT trong request hiện tại — STICKY: không bao giờ reset
    /// (khác <see cref="Current"/> bị clear trong <c>finally</c> của handler). Dispatcher đọc
    /// sau attempt để biết exit nào vừa trả IP-abuse 403 mà chặn 15m (spec §7.3).
    /// <para>
    /// Ghi từ <see cref="ProxyHealthHandler"/> (async frame con) nên giá trị nằm trong object
    /// holder thay vì slot AsyncLocal: write AsyncLocal từ một async method KHÔNG nổi lên frame
    /// cha (đã kiểm chứng bằng probe — kể cả khi frame con không hề suspend), còn mutation object
    /// holder thì frame cha đọc được. Holder do <see cref="BeginPickScope"/> tạo ở frame dispatcher
    /// nên mỗi request một holder — không lẫn exit giữa các request song song.
    /// </para>
    /// </summary>
    public static ProxyAttempt? LastPick
    {
        get => s_lastPick.Value?.Pick;
        set
        {
            // Không có scope (ngoài dispatcher — ví dụ unit test handler đơn lẻ): tạo holder
            // ngay tại frame gọi để ít nhất luồng hiện tại đọc lại được.
            var holder = s_lastPick.Value ??= new PickHolder();
            holder.Pick = value;
        }
    }

    /// <summary>
    /// Mở scope pick cho MỘT request — dispatcher gọi trước khi serve từng request:
    /// mọi write <see cref="LastPick"/> bên dưới (ProxyHealthHandler, async frame con) ghi vào
    /// holder này nên dispatcher đọc được sau attempt. Gọi thêm lần nữa = holder mới (scope mới).
    /// </summary>
    public static void BeginPickScope() => s_lastPick.Value = new PickHolder();

    /// <summary>Holder mutable để write bên dưới async frame vẫn nhìn thấy được ở frame cha.</summary>
    private sealed class PickHolder
    {
        /// <summary>Exit của attempt gần nhất — chỉ set, không bao giờ clear.</summary>
        public ProxyAttempt? Pick;
    }
}
