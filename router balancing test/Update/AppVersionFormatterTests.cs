using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class AppVersionFormatterTests
{
    [Theory]
    [InlineData(1, 0, 0, 0, "1.0")]
    [InlineData(1, 0, 3, 0, "1.0.3")]
    [InlineData(1, 0, 0, 4, "1.0.0.4")]
    [InlineData(1, 0, 3, 4, "1.0.3.4")]
    [InlineData(2, 5, 0, 0, "2.5")]
    public void Format_ReturnsShortForm(int major, int minor, int build, int revision, string expected)
        => Assert.Equal(expected, AppVersionFormatter.Format(new Version(major, minor, build, revision)));

    // Version 2/3 thành phần cho Build/Revision = -1 (undefined) — ctor 4 tham số không nhận -1
    [Theory]
    [InlineData(1, 0, "1.0")]
    [InlineData(2, 5, "2.5")]
    public void Format_WhenBuildUndefined_ReturnsMajorMinor(int major, int minor, string expected)
        => Assert.Equal(expected, AppVersionFormatter.Format(new Version(major, minor)));

    [Theory]
    [InlineData(1, 0, 3, "1.0.3")]
    [InlineData(1, 0, 0, "1.0")]
    public void Format_WhenRevisionUndefined_ReturnsShortForm(int major, int minor, int build, string expected)
        => Assert.Equal(expected, AppVersionFormatter.Format(new Version(major, minor, build)));

    [Fact]
    public void Format_WhenVersionNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => AppVersionFormatter.Format((Version)null!));

    [Theory]
    [InlineData("1.0.0.0", "1.0")]
    [InlineData("1.0.3.0", "1.0.3")]
    [InlineData("2.5", "2.5")]
    public void Format_ParsesStringThenFormats(string input, string expected)
        => Assert.Equal(expected, AppVersionFormatter.Format(input));

    [Fact]
    public void Format_WhenStringUnparsable_Throws()
        => Assert.Throws<ArgumentException>(() => AppVersionFormatter.Format("not-a-version"));

    [Fact]
    public void Format_WhenStringNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => AppVersionFormatter.Format((string)null!));
}
