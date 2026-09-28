using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Cấu hình WebApplication cho proxy — tách khỏi ProxyHost để integration test
/// dựng cùng pipeline qua TestServer (spec 3A §2.1), tránh 2 nơi diverge.
/// Tách 2 method vì middleware/map phải chạy sau Build(): DI đóng băng sau Build.
/// </summary>
public static class ProxyApp
{
    /// <summary>
    /// Đăng ký DI cho proxy. Phải gọi TRƯỚC <c>builder.Build()</c>.
    /// </summary>
    /// <param name="builder">Builder do ProxyHost (hoặc test) tạo.</param>
    /// <param name="protector">Giải mã API key provider — đăng ký singleton dùng chung.</param>
    public static void ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector)
    {
        builder.Services.AddSingleton(protector);
    }

    /// <summary>
    /// Gắn pipeline: auth middleware + endpoint. Gọi NGAY SAU <c>Build()</c>, TRƯỚC <c>StartAsync</c>.
    /// </summary>
    /// <param name="app">WebApplication vừa Build.</param>
    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ApiKeyMiddleware>();
        MapEndpoints(app);
    }

    private static void MapEndpoints(WebApplication app)
    {
        // /health mở luôn (middleware bỏ qua path này) — watchdog của Phase 2 dùng để ping
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        // Danh sách model đã bật, đúng shape OpenAI /v1/models để client không cần phân biệt
        app.MapGet("/v1/models", async (HttpContext http) =>
        {
            var factory = http.RequestServices
                .GetRequiredService<IDbContextFactory<RouterBalancingDbContext>>();
            using var db = factory.CreateDbContext();
            var models = await db.Models.AsNoTracking()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Id)
                .Select(m => new
                {
                    id = m.ModelId,
                    // @object: keyword 'object' không đặt được thẳng làm tên member — JSON vẫn ra "object"
                    @object = "model",
                    created = m.CreatedAt.ToUnixTimeSeconds(),
                    owned_by = m.Provider != null ? m.Provider.Name : "unknown",
                })
                .ToListAsync(http.RequestAborted);

            return Results.Json(new { @object = "list", data = models });
        });
    }
}
