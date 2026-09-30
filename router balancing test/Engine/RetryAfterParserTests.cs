using System.Net.Http.Headers;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_DeltaSeconds_ReturnsSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(120), RetryAfterParser.Parse("120", Now));

    [Fact]
    public void Parse_HttpDateFuture_ReturnsTimeUntilDate()
    {
        var header = new RetryConditionHeaderValue(Now.AddSeconds(90)).ToString();

        Assert.Equal(TimeSpan.FromSeconds(90), RetryAfterParser.Parse(header, Now));
    }

    [Fact]
    public void Parse_HttpDatePast_ReturnsZero()
    {
        var header = new RetryConditionHeaderValue(Now.AddSeconds(-60)).ToString();

        Assert.Equal(TimeSpan.Zero, RetryAfterParser.Parse(header, Now));
    }

    [Fact]
    public void Parse_AboveOneHour_ClampsTo3600()
        => Assert.Equal(TimeSpan.FromSeconds(3600), RetryAfterParser.Parse("7200", Now));

    [Fact]
    public void Parse_MissingHeader_ReturnsNull()
        => Assert.Null(RetryAfterParser.Parse(null, Now));

    [Fact]
    public void Parse_MalformedValue_ReturnsNull()
        => Assert.Null(RetryAfterParser.Parse("later", Now));
}
