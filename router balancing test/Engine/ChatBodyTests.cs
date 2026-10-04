using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ChatBodyTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string ModelOf(byte[] body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;

    [Fact]
    public void WithModel_WhenBodyModelIsPinned_StripsIdentifierPrefix()
    {
        var body = Bytes("""{"model":"opencode-free/mimo-v2.6-flash-free","messages":[]}""");

        var result = ChatBody.WithModel(body, "mimo-v2.6-flash-free");

        Assert.Equal("mimo-v2.6-flash-free", ModelOf(result));
    }

    [Fact]
    public void WithModel_WhenBodyModelIsComboName_ReplacesWithResolvedId()
    {
        var body = Bytes("""{"model":"combo-fast","messages":[]}""");

        var result = ChatBody.WithModel(body, "gpt-4o-mini");

        Assert.Equal("gpt-4o-mini", ModelOf(result));
    }

    [Fact]
    public void WithModel_WhenBodyModelAlreadyMatches_ReturnsOriginalBytes()
    {
        var body = Bytes("""{"model":"gpt-4o-mini","messages":[]}""");

        var result = ChatBody.WithModel(body, "gpt-4o-mini");

        // Đường model id trực tiếp giữ nguyên bytes — không reserialize (spec proxy-core §4)
        Assert.Same(body, result);
    }

    [Fact]
    public void WithModel_WhenJsonInvalid_ReturnsOriginalBytes()
    {
        var body = Bytes("{broken");

        var result = ChatBody.WithModel(body, "gpt-4o-mini");

        Assert.Same(body, result);
    }

    [Fact]
    public void WithModel_WhenOtherFieldsPresent_KeepsThemIntact()
    {
        var body = Bytes(
            """{"model":"alias","messages":[{"role":"user","content":"hi"}],"temperature":0.5}""");

        var result = ChatBody.WithModel(body, "gpt-4o-mini");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("gpt-4o-mini", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(0.5, doc.RootElement.GetProperty("temperature").GetDouble());
    }
}
