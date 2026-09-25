using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Xác thực API key cho mọi endpoint của proxy (trừ /health).
/// Đọc setting mỗi request — bật/tắt key có hiệu lực ngay, không cần restart server.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IAppSettingsService settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // /health luôn mở để watchdog/monitor không cần secret (spec: port + health)
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var expected = settings.ApiKeyEnabled ? settings.GetApiKey() : string.Empty;
        if (string.IsNullOrEmpty(expected))
        {
            await next(context);
            return;
        }

        var provided = ExtractKey(context.Request);
        if (provided is not null && FixedTimeEquals(provided, expected))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "Invalid or missing API key",
                type = "invalid_request_error",
                code = "invalid_api_key",
            },
        });
        await context.Response.WriteAsync(payload, context.RequestAborted);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return authorization[bearer.Length..].Trim();

        var headerKey = request.Headers["X-API-Key"].ToString();
        return string.IsNullOrEmpty(headerKey) ? null : headerKey;
    }

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
