namespace RouterBalancing.Core.Providers;

/// <summary>Kết quả test connection gần nhất — không chứa key.</summary>
/// <param name="Success">HTTP 2xx hay không.</param>
/// <param name="Message">Lý do fail (status/mô tả exception); <see langword="null"/> khi success.</param>
/// <param name="At">Thời điểm test (UTC).</param>
public sealed record ProviderTestResult(bool Success, string? Message, DateTimeOffset At);
