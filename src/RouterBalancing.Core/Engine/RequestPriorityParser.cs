namespace RouterBalancing.Core.Engine;

/// <summary>Parse header <c>X-Priority</c> theo luật lenient của spec §3.1.</summary>
public static class RequestPriorityParser
{
    /// <summary>
    /// Chỉ <c>high</c>/<c>max</c> (case-insensitive, đã trim) được nhận — mọi giá trị khác
    /// (kể cả rỗng/không có header) trả <see cref="RequestPriority.Normal"/>; không reject 400.
    /// </summary>
    public static RequestPriority Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => RequestPriority.High,
        "max" => RequestPriority.Highest,
        _ => RequestPriority.Normal,
    };
}
