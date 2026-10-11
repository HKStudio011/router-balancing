using RouterBalancing.Core.Ui;

namespace router_balancing_test.Ui;

public class PagerWindowTests
{
    [Fact]
    public void Build_WhenPageCountAtThreshold_ReturnsAllPages_NoEllipsis()
    {
        var result = PagerWindow.Build(4, PagerWindow.ShowAllThreshold);

        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6, 7 }, result);
        Assert.DoesNotContain(null, result);
    }

    [Fact]
    public void Build_WhenPageCountExceedsThreshold_ShowsFirstLastAndEllipses()
    {
        var result = PagerWindow.Build(100, 125);

        Assert.Equal(new int?[] { 1, null, 98, 99, 100, 101, 102, null, 125 }, result);
    }

    [Fact]
    public void Build_WhenPageNearStart_OmitsLeadingEllipsis()
    {
        // Trang 2: không có khoảng bị bỏ giữa 1 và cửa sổ → không có "…" đầu
        var result = PagerWindow.Build(2, 25);

        Assert.Equal(new int?[] { 1, 2, 3, 4, null, 25 }, result);
    }

    [Fact]
    public void Build_WhenPageNearEnd_OmitsTrailingEllipsis()
    {
        var result = PagerWindow.Build(24, 25);

        Assert.Equal(new int?[] { 1, null, 22, 23, 24, 25 }, result);
    }

    [Fact]
    public void Build_WhenPageOutOfRange_ClampsToValidRange()
    {
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, PagerWindow.Build(0, 5));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, PagerWindow.Build(99, 5));
    }

    [Fact]
    public void Build_WhenPageCountZero_ReturnsSinglePage()
    {
        Assert.Equal(new int?[] { 1 }, PagerWindow.Build(1, 0));
    }
}
