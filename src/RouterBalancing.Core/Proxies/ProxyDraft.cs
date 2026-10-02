namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Bản nháp form proxy. Property mutable (không phải positional record) để Blazor <c>@bind</c>
/// ghi được — giống <c>ProviderDraft</c>.
/// </summary>
public sealed record ProxyDraft
{
    public string Scheme { get; set; } = "http";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Trống = không có username; khi update để trống ⇒ xóa toàn bộ auth (spec §3.2).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// <see langword="null"/> = giữ password đã lưu (update) / không đặt (create);
    /// trống khi save cũng được coi là null (UI gán trước khi gọi service).
    /// </summary>
    public string? Password { get; set; }
}
