namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Một proxy được chọn cho 1 attempt — sống trong memory, không bao giờ persist;
/// log an toàn qua <see cref="ToString()"/> đã redact <see cref="Password"/> (spec §7).
/// <see cref="Uri"/> KHÔNG chứa userinfo (.NET 10 không parse — dotnet/runtime#125341);
/// auth đi qua <c>DynamicProxyCredentials</c> đọc <see cref="Username"/>/<see cref="Password"/> (spec §4.6).
/// </summary>
/// <param name="Id">Id hàng <c>OutboundProxy</c>.</param>
/// <param name="Uri">URI kết nối <c>scheme://host:port</c> — không userinfo.</param>
/// <param name="Username">Tên đăng nhập đã giải mã từ DB — null = không auth.</param>
/// <param name="Password">Password đã decrypt, chỉ sống trong memory — null = không auth.</param>
/// <param name="Endpoint">Display/log <c>scheme://host:port</c> — không chứa credentials.</param>
public sealed record ProxyAttempt(long Id, Uri Uri, string? Username, string? Password, string Endpoint)
{
    /// <summary>Không để password lọt vào log qua <c>{Attempt}</c> — ràng buộc "không bao giờ log password" (spec §7).</summary>
    public override string ToString() =>
        $"ProxyAttempt {{ Id = {Id}, Endpoint = {Endpoint}, Username = {Username ?? "null"}, Password = [REDACTED] }}";
}
