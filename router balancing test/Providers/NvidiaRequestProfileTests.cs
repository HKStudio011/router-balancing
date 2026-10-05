using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// NVIDIA NIM request profile: (1) attribution headers (X-BILLING-INVOKE-ORIGIN + HTTP-Referer +
/// X-Title — pattern của opencode `provider/nvidia.ts` và hermes `test_provider_attribution_headers.py`);
/// (2) bỏ name/tool_name khỏi role=tool — NIM chấp nhận ToolMessage schema hẹp hơn OpenAI-compatible
/// chuẩn (hermes `NvidiaProviderProfile.prepare_messages`), giữ nguyên → 400.
/// </summary>
public class NvidiaRequestProfileTests
{
    private static Provider Nvidia() => new()
    {
        Name = "NVIDIA NIM Free",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://integrate.api.nvidia.com",
    };

    private static ByteArrayContent JsonBody(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task<JsonArray> MessagesAsync(HttpRequestMessage request)
    {
        var json = await request.Content!.ReadAsStringAsync();
        return JsonNode.Parse(json)!["messages"]!.AsArray();
    }

    [Fact]
    public void Create_NvidiaChat_SetsAttributionHeaders()
    {
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody("""{"model":"m","messages":[]}"""));

        // opencode + hermes đều gắn 3 header này để NVIDIA ghi nhận origin đúng trên billing dashboard
        Assert.Equal("RouterBalancing", Header(request, "X-BILLING-INVOKE-ORIGIN"));
        Assert.Equal("RouterBalancing", Header(request, "X-Title"));
        Assert.Equal("https://github.com/HKStudio011/router-balancing", Header(request, "HTTP-Referer"));
    }

    [Fact]
    public void Create_NvidiaProbe_GetAlsoCarriesAttributionHeaders()
    {
        // opencode/hermes attach theo provider (mọi request), không chỉ chat
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test");

        Assert.Equal("RouterBalancing", Header(request, "X-BILLING-INVOKE-ORIGIN"));
        Assert.Equal("RouterBalancing", Header(request, "X-Title"));
        Assert.Equal("https://github.com/HKStudio011/router-balancing", Header(request, "HTTP-Referer"));
    }

    [Fact]
    public void Create_OpenAiChat_NoNvidiaAttributionHeaders()
    {
        using var request = ProviderRequestFactory.Create(
            new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.openai.com" },
            "sk-test", "/v1/chat/completions", HttpMethod.Post, JsonBody("""{"model":"m","messages":[]}"""));

        Assert.False(request.Headers.Contains("X-BILLING-INVOKE-ORIGIN"));
        Assert.False(request.Headers.Contains("X-Title"));
        Assert.False(request.Headers.Contains("HTTP-Referer"));
    }

    [Fact]
    public async Task Create_NvidiaChat_StripsNameFromToolMessage()
    {
        var body = """
            {"model":"m","messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_1","name":"get_weather","content":"sunny"}
            ]}
            """;
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        var messages = await MessagesAsync(request);
        var tool = messages.Single(m => m!["role"]!.GetValue<string>() == "tool")!.AsObject();
        Assert.False(tool.ContainsKey("name"));
        Assert.Equal("call_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal("sunny", tool["content"]!.GetValue<string>());
        // assistant tool_calls.function.name giữ nguyên — NIM cần để map call
        var call = messages[1]!["tool_calls"]![0]!["function"]!;
        Assert.Equal("get_weather", call["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_NvidiaChat_StripsToolNameFromToolMessage()
    {
        var body = """
            {"model":"m","messages":[
              {"role":"tool","tool_call_id":"call_1","tool_name":"get_weather","content":"sunny"}
            ]}
            """;
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        var messages = await MessagesAsync(request);
        var tool = messages[0]!.AsObject();
        Assert.False(tool.ContainsKey("tool_name"));
        Assert.Equal("sunny", tool["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_NvidiaChat_KeepsNameOnUserMessage()
    {
        // name hợp lệ với user/system (OpenAI spec) — chỉ role=tool là NIM chối
        var body = """
            {"model":"m","messages":[{"role":"user","name":"truc","content":"hi"}]}
            """;
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        var messages = await MessagesAsync(request);
        Assert.Equal("truc", messages[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_NvidiaChat_NoToolFields_LeavesBodyIntact()
    {
        var body = """{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        Assert.Equal(body, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_OpenAiChat_ToolMessageName_Untouched()
    {
        // Strip chỉ áp cho NVIDIA — provider khác forward nguyên xi (proxy là passthrough)
        var body = """
            {"model":"m","messages":[{"role":"tool","tool_call_id":"c","name":"f","content":"x"}]}
            """;
        using var request = ProviderRequestFactory.Create(
            new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.openai.com" },
            "sk-test", "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        Assert.Equal(body, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_NvidiaChat_InvalidJson_LeavesBodyUntouched()
    {
        using var request = ProviderRequestFactory.Create(Nvidia(), "nvapi-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody("not-json"));

        Assert.Equal("not-json", await request.Content!.ReadAsStringAsync());
        // Header vẫn gắn — không phụ thuộc parse được body
        Assert.Equal("RouterBalancing", Header(request, "X-BILLING-INVOKE-ORIGIN"));
    }
}
