using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class SseDataEventParserTests
{
    [Fact]
    public void HandleChunk_MultipleDataLines_JoinsPayloadAndIgnoresOtherFields()
    {
        var actual = Parse(
            "id: 42\r\nevent: message\r\n: comment\r\n" +
            "data: first\r\ndata: second\r\n\r\n");

        Assert.Equal(["data: first\nsecond"], actual);
    }

    [Fact]
    public void HandleChunk_DoneSplitAcrossChunks_PreservesExactDonePayload()
    {
        var actual = Parse("data: [DO", "NE]\n\n");

        Assert.Equal(["data: [DONE]"], actual);
    }

    [Fact]
    public void HandleChunk_CrLfAndCrSeparatedAcrossChunks_RecognizesEventBoundaries()
    {
        var actual = Parse("data: first\r", "\n\r", "\ndata: second\r\r");

        Assert.Equal(["data: first", "data: second"], actual);
    }

    [Fact]
    public void HandleChunk_MultiLineToolCallFedOneByteAtATime_MatchesSingleChunk()
    {
        const string input =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"write\",\n" +
            "data: \"arguments\":\"{\\\"path\\\":\\\"a.txt\\\",\\\"content\\\":\\\"ok\\\"}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";
        string[] expected =
        [
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"write\",\n" +
            "\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\",\\\"content\\\":\\\"ok\\\"}\"}}]}}]}",
            "data: [DONE]",
        ];

        var singleChunk = Parse(input);
        var oneByteAtATime = ParseOneByteAtATime(input);

        Assert.Equal(expected, singleChunk);
        Assert.Equal(expected, oneByteAtATime);
    }

    [Fact]
    public void Finish_DataWithoutBlankLine_ReturnsPendingEvent()
    {
        var parser = new SseDataEventParser();

        Assert.Empty(parser.HandleChunk(Encoding.UTF8.GetBytes("data: tail")));
        Assert.Equal("data: tail", Encoding.UTF8.GetString(parser.Finish()!));
    }

    [Fact]
    public void EmptyDataAndEmptyStream_DoNotProduceEvents()
    {
        Assert.Empty(Parse("data: \n\n"));
        Assert.Empty(Parse());
    }

    [Theory]
    [InlineData("data:  [DONE]\n\n", "data:  [DONE]")]
    [InlineData("data:\t[DONE]\n\n", "data: \t[DONE]")]
    public void HandleChunk_WhitespaceAfterDataPrefix_RemovesOnlyOneOptionalSpace(
        string input, string expected)
    {
        Assert.Equal([expected], Parse(input));
    }

    private static List<string> Parse(params string[] chunks)
    {
        var parser = new SseDataEventParser();
        var events = new List<string>();
        foreach (var chunk in chunks)
            AddEvents(parser.HandleChunk(Encoding.UTF8.GetBytes(chunk)), events);
        AddFinish(parser, events);
        return events;
    }

    private static List<string> ParseOneByteAtATime(string input)
    {
        var parser = new SseDataEventParser();
        var events = new List<string>();
        foreach (var value in Encoding.UTF8.GetBytes(input))
            AddEvents(parser.HandleChunk([value]), events);
        AddFinish(parser, events);
        return events;
    }

    private static void AddFinish(SseDataEventParser parser, List<string> events)
    {
        if (parser.Finish() is { } tail)
            events.Add(Encoding.UTF8.GetString(tail));
    }

    private static void AddEvents(IEnumerable<byte[]> payloads, List<string> events)
    {
        foreach (var payload in payloads)
            events.Add(Encoding.UTF8.GetString(payload));
    }
}
