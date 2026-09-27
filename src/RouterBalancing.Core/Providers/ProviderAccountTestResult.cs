namespace RouterBalancing.Core.Providers;

/// <summary>
/// Kết quả test của một account trong lần TestAll — trả về UI thay vì chỉ ghi DB,
/// để bảng test hiển thị được ngay từng dòng.
/// </summary>
/// <param name="AccountId">Id account vừa test.</param>
/// <param name="AccountName">Tên account để UI hiển thị không cần join lại.</param>
/// <param name="Success">HTTP call thành công hay không (false = đã ghi LastTest*).</param>
/// <param name="Message">Lý do fail (HTTP status/exception); null khi success.</param>
public sealed record ProviderAccountTestResult(long AccountId, string AccountName, bool Success, string? Message);
