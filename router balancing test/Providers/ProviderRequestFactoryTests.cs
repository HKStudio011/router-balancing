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
        ApiKeyEncrypted = string.Empty,
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
}
