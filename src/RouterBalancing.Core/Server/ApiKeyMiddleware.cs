using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Xác thực API key inbound từ bảng ClientKeys (snapshot in-memory qua <see cref="ClientKeyAuthCache"/>)
/// + chặn RPM/TPM trước khi vào pipeline (spec client-keys §6).
/// Không key enabled nào = proxy mở (backward-compat với cài đặt cũ chưa set key).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ClientKeyAuthCache keys,
    IClientKeyRateLimiter limiter, IClientKeyService service, ILogService log)
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

        var enabled = keys.GetEnabled();
        if (enabled.Length == 0)
        {
            await next(context);
            return;
        }

        var provided = ExtractKey(context.Request);
        var match = provided is null ? null : FindByHash(provided, enabled);
        if (match is null)
        {
            await RejectAsync(context, StatusCodes.Status401Unauthorized,
                "Invalid or missing API key", "invalid_request_error", "invalid_api_key");
            return;
        }

        var (allowed, retryAfterSec) = limiter.TryEnter(match.Id, match.RatePerMinute, match.TokensPerMinute);
        if (!allowed)
        {
            context.Response.Headers.RetryAfter = retryAfterSec.ToString();
            await RejectAsync(context, StatusCodes.Status429TooManyRequests,
                "Rate limit exceeded", "rate_limit_exceeded", "rate_limit_exceeded");
            return;
        }

        context.Items[ClientKeyItems.Id] = match.Id;

        // Counter daily là thống kê — DB lỗi không được chặn request (fail-open, spec §10)
        try
        {
            await service.RecordRequestAsync(match.Id);
        }
        catch (Exception ex)
        {
            log.Error("Không ghi được counter request cho client key.", ex, LogCategory.Request);
        }

        await next(context);
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

    private static ClientKeyAuthInfo? FindByHash(string provided, ClientKeyAuthInfo[] enabled)
    {
        var hash = ClientKeyHasher.Hash(provided);
        foreach (var info in enabled)
        {
            // FixedTimeEquals trên hash hex (luôn 64 ký tự) — không rò rỉ key nào match qua timing
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(info.KeyHash)))
                return info;
        }
        return null;
    }

    private static Task RejectAsync(HttpContext context, int status, string message, string type, string code) =>
        // Tái dùng WriteErrorAsync có sẵn: shape OpenAI + encoder relax qua ErrorJsonOptions (spec §10)
        ChatCompletionsHandler.WriteErrorAsync(context, status, message, type, param: null, code);
}
