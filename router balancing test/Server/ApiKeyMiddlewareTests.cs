using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ApiKeyMiddlewareTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _keys;
    private readonly ClientKeyAuthCache _cache;
    private readonly ClientKeyRateLimiter _limiter = new(TimeProvider.System);
    private readonly LogService _log;

    public ApiKeyMiddlewareTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _keys = new ClientKeyService(_db.CreateFactory());
        _cache = new ClientKeyAuthCache(_db.CreateFactory(), _keys);
        _log = new LogService(_db.CreateFactory());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private async Task<string> CreateKeyAsync(bool enabled = true, int? rpm = null, int? tpm = null)
    {
        var (key, plaintext) = await _keys.CreateAsync(new ClientKeyDraft("test", rpm, tpm));
        if (!enabled) await _keys.SetEnabledAsync(key.Id, false);
        return plaintext;
    }

    private async Task<(int StatusCode, bool NextCalled, string Body, object? KeyId)> InvokeAsync(
        string path, string? authorization = null, string? apiKeyHeader = null)
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
            _cache, _limiter, _keys, _log);
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        context.Items.TryGetValue(ClientKeyItems.Id, out var keyId);
        return (context.Response.StatusCode, nextCalled, body, keyId);
    }

    [Fact]
    public async Task Invoke_WhenNoEnabledKeys_PassesThrough()
    {
        var (status, nextCalled, _, _) = await InvokeAsync("/v1/chat/completions");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_OnHealthPath_SkipsAuth_EvenWithKey()
    {
        await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/health");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenKeyMissing_Returns401WithErrorBody()
    {
        await CreateKeyAsync();

        var (status, nextCalled, body, _) = await InvokeAsync("/v1/models");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
        Assert.Contains("invalid_api_key", body);
    }

    [Fact]
    public async Task Invoke_WhenKeyWrong_Returns401()
    {
        await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", authorization: "Bearer wrong-key");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenBearerKeyCorrect_PassesThroughAndSetsKeyId()
    {
        var plaintext = await CreateKeyAsync();

        var (status, nextCalled, _, keyId) = await InvokeAsync(
            "/v1/models", authorization: $"Bearer {plaintext}");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
        Assert.NotNull(keyId);
    }

    [Fact]
    public async Task Invoke_WhenXApiKeyHeaderCorrect_PassesThrough()
    {
        var plaintext = await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenAllKeysDisabled_PassesThrough_OpenMode()
    {
        await CreateKeyAsync(enabled: false);

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenOneKeyEnabled_DisabledKeyRejected()
    {
        var disabledPlaintext = await CreateKeyAsync(enabled: false);
        await CreateKeyAsync(enabled: true);

        // Gửi đúng plaintext của key disabled: nếu snapshot bỏ .Where(k => k.Enabled) thì
        // request này match và trả 200 — test fail, bắt được lỗi auth-critical đó.
        var (status, _, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: disabledPlaintext);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Cache_ReflectsKeyCreatedAfterFirstCall_WithoutRestart()
    {
        // Call 1: chưa có key → open
        var (firstStatus, _, _, _) = await InvokeAsync("/v1/models");
        Assert.Equal(200, firstStatus);

        // CRUD key → KeysChanged → cache dirty
        await CreateKeyAsync();

        var (secondStatus, _, _, _) = await InvokeAsync("/v1/models");
        Assert.Equal(StatusCodes.Status401Unauthorized, secondStatus);
    }

    [Fact]
    public async Task Invoke_WhenRpmExceeded_Returns429WithRetryAfter()
    {
        var plaintext = await CreateKeyAsync(rpm: 1);

        var (first, _, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);
        Assert.Equal(200, first);

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/models";
        context.Request.Headers["X-API-Key"] = plaintext;
        context.Response.Body = new MemoryStream();
        var middleware = new ApiKeyMiddleware(_ => Task.CompletedTask, _cache, _limiter, _keys, _log);
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.True(int.TryParse(context.Response.Headers.RetryAfter, out var retry) && retry >= 1);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("rate_limit_exceeded", body);
    }

    [Fact]
    public async Task Invoke_WhenTpmReached_Returns429()
    {
        var plaintext = await CreateKeyAsync(tpm: 5);
        var key = (await _keys.ListAsync()).Single();
        _limiter.AddTokens(key.Id, 5);

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_AfterSuccessfulAuth_IncrementsDailyCounter()
    {
        var plaintext = await CreateKeyAsync();

        await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        var saved = (await _keys.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
        Assert.NotNull(saved.LastUsedAt);
    }

    [Fact]
    public async Task Invoke_WhenRequestDeniedByRateLimit_DoesNotCountRequest()
    {
        var plaintext = await CreateKeyAsync(rpm: 1);
        await InvokeAsync("/v1/models", apiKeyHeader: plaintext);
        await InvokeAsync("/v1/models", apiKeyHeader: plaintext); // 429

        var saved = (await _keys.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
    }
}
