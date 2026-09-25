using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ApiKeyMiddlewareTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;

    public ApiKeyMiddlewareTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    private static async Task<(int StatusCode, bool NextCalled, string Body)> InvokeAsync(
        IAppSettingsService settings, string path, string? authorization = null, string? apiKeyHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (apiKeyHeader is not null) context.Request.Headers["X-API-Key"] = apiKeyHeader;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            settings);
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, nextCalled, body);
    }

    [Fact]
    public async Task Invoke_WhenApiKeyDisabled_PassesThrough()
    {
        var (status, nextCalled, _) = await InvokeAsync(_settings, "/v1/chat/completions");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_OnHealthPath_SkipsAuth()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(_settings, "/health");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenEnabledButNoKeyConfigured_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);

        var (status, nextCalled, _) = await InvokeAsync(_settings, "/v1/models");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenKeyMissing_Returns401WithErrorBody()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, body) = await InvokeAsync(_settings, "/v1/models");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
        Assert.Contains("invalid_api_key", body);
    }

    [Fact]
    public async Task Invoke_WhenKeyWrong_Returns401()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", authorization: "Bearer wrong-key");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenBearerKeyCorrect_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", authorization: "Bearer secret-key");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenXApiKeyHeaderCorrect_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", apiKeyHeader: "secret-key");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }
}
