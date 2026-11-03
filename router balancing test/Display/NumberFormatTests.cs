using System.Globalization;
using RouterBalancing.Core.Display;

namespace router_balancing_test.Display;

/// <summary>
/// Test <see cref="NumberFormat.Compact"/> — pin <see cref="CultureInfo.InvariantCulture"/>
/// để kết quả không phụ thuộc culture của máy CI.
/// </summary>
public class NumberFormatTests
{
    [Fact]
    public void Compact_Below1000_NoSuffix()
        => Assert.Equal("999", NumberFormat.Compact(999, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_Thousands_kSuffix()
        => Assert.Equal("1.5k", NumberFormat.Compact(1_500, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_JustBelowMillion_ClampsTo999_9k()
        => Assert.Equal("999.9k", NumberFormat.Compact(999_999, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_Millions_MSuffix()
        => Assert.Equal("1.5M", NumberFormat.Compact(1_500_000, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_Billions_BSuffix()
        => Assert.Equal("1.2B", NumberFormat.Compact(1_200_000_000, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_TensOfBillions_BSuffix()
        => Assert.Equal("15.0B", NumberFormat.Compact(15_000_000_000, CultureInfo.InvariantCulture));

    [Fact]
    public void Compact_UsesProvidedFormatProvider()
        => Assert.Equal("1,5k", NumberFormat.Compact(1_500, CultureInfo.GetCultureInfo("vi-VN")));
}
