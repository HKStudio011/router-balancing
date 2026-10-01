using Microsoft.AspNetCore.Http;

namespace RouterBalancing.Core.Server;

/// <summary>Khóa HttpContext.Items do middleware điền — các tầng sau (handler, journal) đọc lại.</summary>
public static class ClientKeyItems
{
    /// <summary>Client key id đã auth (dùng cho usage row + journal).</summary>
    public static readonly string Id = "ClientKeyId";

    /// <summary>Request id 8 ký tự (ProxyApp.New) — correlate journal rows + usage row.</summary>
    public static readonly string RequestId = "RequestId";

    public static long? IdOf(HttpContext context) =>
        context.Items.TryGetValue(Id, out var value) ? value as long? : null;

    public static string? RequestIdOf(HttpContext context) =>
        context.Items.TryGetValue(RequestId, out var value) ? value as string : null;
}
