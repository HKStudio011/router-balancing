using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

/// <summary>
/// Chính sách timeout đường forward chat: app KHÔNG tự cắt request — client→app và
/// app→provider không có timeout; việc dừng request (client→provider) do client quyết
/// định qua disconnect (<c>RequestAborted</c>), app không liên quan.
/// </summary>
public class ProxyUpstreamTimeoutTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void CreateUpstreamHandler_ConnectTimeout_IsInfinite()
    {
        using var handler = ProxyApp.CreateUpstreamHandler();

        // Connect finite = app cắt kết nối đang mở tới provider — vi phạm policy
        Assert.Equal(Timeout.InfiniteTimeSpan, handler.ConnectTimeout);
    }

    [Fact]
    public void UpstreamClient_HttpClientTimeout_IsInfinite()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILogService>(new NullLog());
        ProxyApp.ConfigureServices(builder, _protector, new DirectProxyPool());
        using var provider = builder.Services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OpenAiUpstreamClient.HttpClientName);

        // Timeout 100s (default) cắt SSE giữa chừng — chỉ client mới được phép dừng request
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
