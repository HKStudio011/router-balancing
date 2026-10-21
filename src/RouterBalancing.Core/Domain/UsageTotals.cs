namespace RouterBalancing.Core.Domain;

/// <summary>Tổng usage trong một ngày của một chủ thể — dùng cho hiển thị UI.</summary>
/// <param name="Requests">Số request đã ghi nhận.</param>
/// <param name="Tokens">Số token (prompt + completion) đã ghi nhận.</param>
public readonly record struct UsageTotals(long Requests, long Tokens);
