namespace RouterBalancing.Core.Providers;

/// <summary>Kết quả test của một account trong lần TestAll.</summary>
public sealed record ProviderAccountTestResult(long AccountId, string AccountName, bool Success, string? Message);
