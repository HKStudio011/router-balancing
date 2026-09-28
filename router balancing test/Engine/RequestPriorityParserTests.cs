using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RequestPriorityParserTests
{
    [Fact]
    public void Parse_IsLenient_MapsHighMaxOnlyAndNormalForEverythingElse()
    {
        // Spec §3.1: chỉ "high"/"max" (case-insensitive, trim) — giá trị lạ → Normal, không 400
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse(null));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse(""));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("normal"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("high"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("High"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("  high  "));
        Assert.Equal(RequestPriority.Highest, RequestPriorityParser.Parse("max"));
        Assert.Equal(RequestPriority.Highest, RequestPriorityParser.Parse("MAX"));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("urgent"));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("2"));
    }
}
