using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// Attribution headers cho OpenRouter (host <c>openrouter.ai</c>) — opencode
/// (<c>plugin/provider/openrouter.ts</c>) và hermes đều gắn HTTP-Referer/X-Title để
/// OpenRouter ghi nhận app trên rankings; KHÔNG có X-BILLING-INVOKE-ORIGIN (chỉ NVIDIA).
/// </summary>
public class ProviderAttributionTests
{
    private static Provider OpenRouter() => new()
    {
        Name = "OpenRouter Free",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://openrouter.ai/api",
    };

    private static ByteArrayContent JsonBody(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    [Fact]
    public void Create_OpenRouterChat_SetsAttributionHeaders()
    {
        using var request = ProviderRequestFactory.Create(OpenRouter(), "or-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody("""{"model":"m","messages":[]}"""));

        Assert.Equal("RouterBalancing", Header(request, "X-Title"));
        Assert.Equal("https://github.com/HKStudio011/router-balancing", Header(request, "HTTP-Referer"));
    }

    [Fact]
    public void Create_OpenRouterProbe_GetAlsoCarriesAttributionHeaders()
    {
        using var request = ProviderRequestFactory.Create(OpenRouter(), "or-test");

        Assert.Equal("RouterBalancing", Header(request, "X-Title"));
        Assert.Equal("https://github.com/HKStudio011/router-balancing", Header(request, "HTTP-Referer"));
    }

    [Fact]
    public void Create_OpenRouterChat_NoBillingOriginHeader()
    {
        // X-BILLING-INVOKE-ORIGIN là của NVIDIA — OpenRouter không dùng, không gửi thừa
        using var request = ProviderRequestFactory.Create(OpenRouter(), "or-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody("""{"model":"m","messages":[]}"""));

        Assert.False(request.Headers.Contains("X-BILLING-INVOKE-ORIGIN"));
    }

    [Fact]
    public async Task Create_OpenRouterChat_ToolMessageName_Untouched()
    {
        // Strip name/tool_name chỉ áp cho NIM — OpenRouter nhận schema chuẩn, proxy passthrough
        var body = """{"model":"m","messages":[{"role":"tool","tool_call_id":"c","name":"f","content":"x"}]}""";
        using var request = ProviderRequestFactory.Create(OpenRouter(), "or-test",
            "/v1/chat/completions", HttpMethod.Post, JsonBody(body));

        Assert.Equal(body, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public void Create_OpenRouterLookalikeHost_NoAttributionHeaders()
    {
        // Host phải match exact — suffix lừa (openrouter.ai.evil.com) không được gắn attribution
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://openrouter.ai.evil.com/api",
        };
        using var request = ProviderRequestFactory.Create(provider, "k",
            "/v1/chat/completions", HttpMethod.Post, JsonBody("""{"model":"m","messages":[]}"""));

        Assert.False(request.Headers.Contains("HTTP-Referer"));
        Assert.False(request.Headers.Contains("X-Title"));
        Assert.False(request.Headers.Contains("X-BILLING-INVOKE-ORIGIN"));
    }
}
