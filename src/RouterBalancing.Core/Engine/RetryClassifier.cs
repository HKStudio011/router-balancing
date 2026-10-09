using System.Net;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phân loại lỗi upstream retryable vs non-retryable — 1 điểm duy nhất cho handler
/// (spec 3C §3.1). Phân loại theo status: <see langword="null"/> (lỗi mạng/timeout),
/// 408 và 500–599 là transient; 429 và 4xx còn lại không transient (xoay tài khoản).
/// Lỗi mạng KHÔNG đi qua đây dạng <see cref="DispatchOutcome.Retryable"/>: handler
/// catch filter (3A) trả <see cref="DispatchOutcome.Fatal"/> cấp Provider với
/// <c>Status = null</c>; dispatcher bắt shape đó riêng trong <c>IsTransientOutcome</c>.
/// </summary>
public static class RetryClassifier
{
    /// <summary>
    /// <see langword="true"/> với 429/408/5xx (advance candidate kế);
    /// <see langword="false"/> với 4xx còn lại — kể cả 401/403 key sai (passthrough ngay, §1.4).
    /// Chỉ gọi khi đã là lỗi (không phân loại 2xx/3xx).
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.RequestTimeout => true,
        _ => (int)status is >= 500 and <= 599,
    };

    /// <summary>
    /// <see langword="true"/> với lỗi mạng/timeout (<see langword="null"/>, catch filter 3A),
    /// 408 và 5xx — retryable có backoff; <see langword="false"/> với 429 (rotate tài khoản
    /// ngay, §1.3) và các 4xx còn lại.
    /// </summary>
    public static bool IsTransient(int? status) => status is null or 408 or (>= 500 and <= 599);
}
