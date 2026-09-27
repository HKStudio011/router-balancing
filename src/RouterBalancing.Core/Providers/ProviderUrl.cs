namespace RouterBalancing.Core.Providers;

/// <summary>
/// Chuẩn hoá BaseUrl provider — chống ghép ra <c>/v1/v1/...</c> khi user dán base kèm <c>/v1</c>.
/// </summary>
public static class ProviderUrl
{
    /// <summary>
    /// Bỏ whitespace đầu/cuối, rồi lặp strip trailing slash và hậu tố <c>/v1</c>
    /// cho đến khi ổn định (xử lý cả base từng bị lưu <c>.../v1/v1</c>).
    /// </summary>
    /// <param name="baseUrl">Base do người dùng nhập, có hoặc không <c>/v1</c>.</param>
    /// <returns>Base canonical: không trailing slash, không hậu tố <c>/v1</c>.</returns>
    public static string Canonicalize(string baseUrl)
    {
        var result = baseUrl.Trim();
        bool changed;
        do
        {
            changed = false;
            var noSlash = result.TrimEnd('/');
            if (!string.Equals(noSlash, result, StringComparison.Ordinal))
            {
                result = noSlash;
                changed = true;
            }
            // OrdinalIgnoreCase: gateway có thể in hoa /V1 nhưng HTTP path chuẩn là lowercase
            if (result.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^"/v1".Length];
                changed = true;
            }
        } while (changed);
        return result;
    }
}
