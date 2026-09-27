using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderUrlTests
{
    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com")]
    [InlineData("https://api.openai.com/", "https://api.openai.com")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com")]
    [InlineData("https://gw.example.com/api/v1", "https://gw.example.com/api")]
    [InlineData("https://gw.example.com/v1/v1", "https://gw.example.com")]
    [InlineData("  https://gw.example.com/v1  ", "https://gw.example.com")]
    public void Canonicalize_WhenVariousForms_StripsTrailingSlashAndVersionSuffix(
        string input, string expected)
    {
        Assert.Equal(expected, ProviderUrl.Canonicalize(input));
    }

    [Fact]
    public void Canonicalize_WhenBaseIsPrefixOfVersion_KeepsIt()
    {
        // Host/path không được cắt oan: chỉ strip đúng hậu tố "/v1"
        Assert.Equal("https://api.v2.example.com", ProviderUrl.Canonicalize("https://api.v2.example.com"));
    }
}
