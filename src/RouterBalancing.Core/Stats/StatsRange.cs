namespace RouterBalancing.Core.Stats;

/// <summary>Khoảng thời gian preset của StatsPanel — không có date picker (spec dashboard-stats §1).</summary>
public enum StatsRange
{
    /// <summary>Hôm nay: [nửa đêm UTC, now].</summary>
    Today,

    /// <summary>7 ngày: [nửa đêm UTC −6 ngày, now].</summary>
    Last7Days,

    /// <summary>30 ngày: [nửa đêm UTC −29 ngày, now].</summary>
    Last30Days,
}
