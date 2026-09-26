using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderValidatorTests
{
    private static ProviderDraft ValidDraft() => new()
    {
        Name = "OpenAI",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com",
        ApiKey = "sk-test",
        MaxConcurrent = 4,
    };

    [Fact]
    public void Validate_WhenDraftValid_ReturnsEmpty()
    {
        var errors = ProviderValidator.Validate(ValidDraft());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenNameBlank_ReturnsNameError()
    {
        var blank = ProviderValidator.Validate(ValidDraft() with { Name = "   " });
        var missing = ProviderValidator.Validate(ValidDraft() with { Name = "" });

        Assert.Equal("providers.error.name", blank[nameof(ProviderDraft.Name)]);
        Assert.Equal("providers.error.name", missing[nameof(ProviderDraft.Name)]);
    }

    [Fact]
    public void Validate_WhenBaseUrlNotHttp_ReturnsBaseUrlError()
    {
        var ftp = ProviderValidator.Validate(ValidDraft() with { BaseUrl = "ftp://example.com" });
        var empty = ProviderValidator.Validate(ValidDraft() with { BaseUrl = "" });

        Assert.Equal("providers.error.baseUrl", ftp[nameof(ProviderDraft.BaseUrl)]);
        Assert.Equal("providers.error.baseUrl", empty[nameof(ProviderDraft.BaseUrl)]);
    }

    [Fact]
    public void Validate_WhenMaxConcurrentOutOfRange_ReturnsMaxConcurrentError()
    {
        var low = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 0 });
        var high = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 65 });

        Assert.Equal("providers.error.maxConcurrent", low[nameof(ProviderDraft.MaxConcurrent)]);
        Assert.Equal("providers.error.maxConcurrent", high[nameof(ProviderDraft.MaxConcurrent)]);
    }
}
