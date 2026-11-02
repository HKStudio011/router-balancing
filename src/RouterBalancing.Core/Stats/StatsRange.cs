namespace RouterBalancing.Core.Stats;

/// <summary>Khoảng thời gian preset của StatsPanel — không có date picker (spec dashboard-stats §1).</summary>
public enum StatsRange
{
    // Nửa đêm tính theo múi giờ StatsService được inject (MauiProgram đăng ký TimeZoneInfo.Local;
    // không có múi thì mặc định UTC) — xem StatsService.ResolveWindow.

    /// <summary>Hôm nay: [nửa đêm của múi, now].</summary>
    Today,

    /// <summary>7 ngày: [nửa đêm của múi −6 ngày, now].</summary>
    Last7Days,

    /// <summary>30 ngày: [nửa đêm của múi −29 ngày, now].</summary>
    Last30Days,
}
