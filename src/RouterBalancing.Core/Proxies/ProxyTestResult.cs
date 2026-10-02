namespace RouterBalancing.Core.Proxies;

/// <summary>Kết quả 1 lần test proxy thủ công — nút Test ở trang Proxies (spec §5.4).</summary>
public sealed record ProxyTestResult(bool Success, string? Error, int? HttpStatus, TimeSpan Elapsed, string? Ip = null);
