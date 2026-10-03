using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderRequestFactoryTests
{
    private static Provider P(string baseUrl) => new()
    {
        Name = "P",
        Type = ProviderType.OpenAI,
        BaseUrl = baseUrl,
    };

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("https://api.openai.com/")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/")]
    public void Create_WhenDefaultPath_AppearsExactlyOneV1(string baseUrl)
    {
        using var request = ProviderRequestFactory.Create(P(baseUrl), "sk-test");

        Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.ToString());
    }

    [Fact]
    public void Create_WhenBaseHasVersionAndPathIsVersioned_NoDuplicateV1()
    {
        // Base dán kèm /v1 (row lưu trước khi có save-fix) — compose vẫn không đôi v1
        using var request = ProviderRequestFactory.Create(
            P("https://gw.example.com/v1"), "k", "/v1/models/gpt-4o");

        Assert.Equal("https://gw.example.com/v1/models/gpt-4o", request.RequestUri!.ToString());
    }

    [Fact]
    public void Create_WhenPathHasNoVersion_PreservesBaseVersion()
    {
        // Chỉ canonicalize khi path tự bắt đầu /v1 — không đoán với path lạ
        using var request = ProviderRequestFactory.Create(
            P("https://gw.example.com/v1"), "k", "/models");

        Assert.Equal("https://gw.example.com/v1/models", request.RequestUri!.ToString());
    }

    [Fact]
    public void Create_WhenPostWithContent_KeepsMethodContentAndHeaders()
    {
        var content = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        using var request = ProviderRequestFactory.Create(
            P("https://api.openai.com/v1"), "sk-test", "/v1/chat/completions", HttpMethod.Post, content);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization.Parameter);
        Assert.Same(content, request.Content);
    }

    [Fact]
    public void Create_WhenMethodOmitted_DefaultsToGetWithoutContent()
    {
        using var request = ProviderRequestFactory.Create(P("https://api.openai.com"), "sk-test");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
    }

    [Fact]
    public void Create_WhenKeyEmpty_OpenAiOmitsAuthorizationHeader()
    {
        using var request = ProviderRequestFactory.Create(P("https://api.openai.com"), string.Empty);

        // Key rỗng = free endpoint không auth — KHÔNG gửi "Bearer" trần (D7)
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Create_WhenKeyEmpty_AnthropicOmitsApiKeyButKeepsVersion()
    {
        var provider = P("https://api.anthropic.com");
        provider.Type = ProviderType.Anthropic;

        using var request = ProviderRequestFactory.Create(provider, string.Empty);

        Assert.False(request.Headers.Contains("x-api-key"));
        // anthropic-version bắt buộc kể cả không key
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
    }
}
