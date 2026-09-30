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
}
