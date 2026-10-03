using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderValidatorTests
{
    private static ProviderDraft ValidDraft() => new()
    {
        Name = "OpenAI",
        Identifier = "openai-prod",
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
        var low = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = -1 });
        var high = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 65 });

        Assert.Equal("providers.error.maxConcurrent", low[nameof(ProviderDraft.MaxConcurrent)]);
        Assert.Equal("providers.error.maxConcurrent", high[nameof(ProviderDraft.MaxConcurrent)]);
    }

    [Fact]
    public void Validate_WhenMaxConcurrentZero_ReturnsNoError()
    {
        // 0 = không giới hạn đồng thời (D-B1) — hợp lệ
        var errors = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 0 });

        Assert.False(errors.ContainsKey(nameof(ProviderDraft.MaxConcurrent)));
    }

    [Fact]
    public void Validate_WhenIdentifierBlank_ReturnsIdentifierRequired()
    {
        var blank = ProviderValidator.Validate(ValidDraft() with { Identifier = "   " });
        var missing = ProviderValidator.Validate(ValidDraft() with { Identifier = "" });

        Assert.Equal("providers.error.identifierRequired", blank[nameof(ProviderDraft.Identifier)]);
        Assert.Equal("providers.error.identifierRequired", missing[nameof(ProviderDraft.Identifier)]);
    }

    [Theory]
    [InlineData("OpenAI")]        // uppercase
    [InlineData("has space")]     // space
    [InlineData("a--b")]          // gạch đôi
    [InlineData("-leading")]      // bắt đầu bằng gạch
    [InlineData("trailing-")]     // kết thúc bằng gạch
    [InlineData("openai/prod")]   // '/' → cũng là segment-trùng mầm móng
    [InlineData("Việt-Nam")]      // có dấu
    public void Validate_WhenIdentifierNotSlug_ReturnsIdentifierFormat(string identifier)
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = identifier });

        Assert.Equal("providers.error.identifierFormat", errors[nameof(ProviderDraft.Identifier)]);
    }

    [Fact]
    public void Validate_WhenIdentifierTooLong_ReturnsIdentifierFormat()
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = new string('a', 51) });

        Assert.Equal("providers.error.identifierFormat", errors[nameof(ProviderDraft.Identifier)]);
    }

    [Theory]
    [InlineData("p1")]
    [InlineData("openai-prod")]
    [InlineData("a")]
    public void Validate_WhenIdentifierValidSlug_NoIdentifierError(string identifier)
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = identifier });

        Assert.False(errors.ContainsKey(nameof(ProviderDraft.Identifier)));
    }
}
