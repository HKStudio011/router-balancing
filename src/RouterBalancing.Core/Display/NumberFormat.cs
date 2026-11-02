using System.Globalization;

namespace RouterBalancing.Core.Display;

/// <summary>
/// Định dạng số rút gọn cho UI (StatsPanel/StatsChart): k/M/B với 1 chữ số thập phân.
/// </summary>
public static class NumberFormat
{
    /// <summary>
    /// Rút gọn số liệu: dưới 1.000 → "999"; từ 1.000 → "12,3k"; từ 1.000.000 → "1,2M";
    /// từ 1.000.000.000 → "1,2B" — 1 chữ số thập phân cho k/M/B, phân cách theo
    /// <paramref name="provider"/> (mặc định <see cref="CultureInfo.CurrentCulture"/>, mockup dùng "," — VI culture).
    /// </summary>
    /// <param name="value">Giá trị cần định dạng — mặc định input không âm (token/request count).</param>
    /// <param name="provider">Format provider cho dấu phân cách; null → <see cref="CultureInfo.CurrentCulture"/>.</param>
    /// <returns>Số nguyên hoặc chuỗi có hậu tố k/M/B.</returns>
    public static string Compact(long value, IFormatProvider? provider = null)
    {
        var culture = provider ?? CultureInfo.CurrentCulture;
        if (value is >= -999 and <= 999)
            return value.ToString(culture);

        // B phải xét TRƯỚC M: 1_200_000_000 mà rơi vào nhánh M sẽ ra "1200.0M" (vượt hậu tố M).
        if (value is <= -1_000_000_000 or >= 1_000_000_000)
            return (value / 1_000_000_000d).ToString("0.0", culture) + "B";

        // Quyết định hậu tố TRƯỚC khi format: 999_999 làm tròn 1 chữ số thập phân của
        // k-branch ra 1000.0 → hiển thị "1000,0k" (vượt tiền tố k). Với giá trị < 1_000_000
        // luôn dùng k và clamp 999.9 để không bao giờ đạt 1000.
        if (value is <= -1_000_000 or >= 1_000_000)
            return (value / 1_000_000d).ToString("0.0", culture) + "M";

        var thousands = Math.Clamp(Math.Round(value / 1000d, 1), -999.9, 999.9);
        return thousands.ToString("0.0", culture) + "k";
    }
}
