using System.Net;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]         // 429
    [InlineData(HttpStatusCode.RequestTimeout)]          // 408
    [InlineData(HttpStatusCode.InternalServerError)]     // 500
    [InlineData(HttpStatusCode.BadGateway)]              // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]      // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]          // 504
    public void IsRetryable_RateLimitTimeoutAnd5xx_ReturnsTrue(HttpStatusCode status)
        => Assert.True(RetryClassifier.IsRetryable(status));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]              // 400
    [InlineData(HttpStatusCode.Unauthorized)]            // 401 — key sai, không retry (§1.4)
    [InlineData(HttpStatusCode.Forbidden)]               // 403
    [InlineData(HttpStatusCode.NotFound)]                 // 404
    [InlineData(HttpStatusCode.Conflict)]                 // 409
    [InlineData(HttpStatusCode.UnprocessableEntity)]      // 422
    public void IsRetryable_ClientErrors_ReturnsFalse(HttpStatusCode status)
        => Assert.False(RetryClassifier.IsRetryable(status));

    [Theory]
    [InlineData(null)]                         // lỗi mạng/timeout — catch filter 3A
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void IsTransient_NetworkTimeoutAnd5xx_ReturnsTrue(int? status)
        => Assert.True(RetryClassifier.IsTransient(status));

    [Theory]
    [InlineData(429)]                          // 429 KHÔNG transient — rotate TK ngay (§1.3)
    [InlineData(400)]
    [InlineData(401)]
    public void IsTransient_RateLimitAndClientErrors_ReturnsFalse(int? status)
        => Assert.False(RetryClassifier.IsTransient(status));
}
