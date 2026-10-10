namespace RouterBalancing.Core.Domain;

/// <summary>
/// Kết quả terminal của request tới provider — stats dashboard group theo enum này
/// để phân biệt OK / lỗi / cancel (spec dashboard-stats §4).
/// </summary>
public enum RequestOutcome
{
    Ok = 1,
    Error = 2,
    Cancelled = 3,
}
