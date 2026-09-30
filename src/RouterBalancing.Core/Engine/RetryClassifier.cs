using System.Net;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phân loại lỗi upstream retryable vs non-retryable — 1 điểm duy nhất cho handler
/// (spec 3C §3.1). Lỗi mạng/timeout KHÔNG đi qua đây: handler bắt bằng catch filter
/// sẵn có (3A) rồi trả <see cref="DispatchOutcome.Retryable"/> với <c>Status = null</c>.
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
}
