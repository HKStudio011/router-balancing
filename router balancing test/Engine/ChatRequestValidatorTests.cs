using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ChatRequestValidatorTests
{
    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    private const string ValidBody =
        """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true,"temperature":0.2}""";

    [Fact]
    public void Validate_WhenJsonBroken_ReturnsInvalidJson()
    {
        var result = ChatRequestValidator.Validate(B("{not json"));

        Assert.Equal(ValidationFailure.InvalidJson, result.Failure);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WhenBodyEmpty_ReturnsInvalidJson()
    {
        var result = ChatRequestValidator.Validate([]);

        Assert.Equal(ValidationFailure.InvalidJson, result.Failure);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"scalar\"")]
    [InlineData("null")]
    public void Validate_WhenRootNotObject_ReturnsMissingModel(string json)
    {
        // Root không phải object → không có trường model (V2), TryGetProperty sẽ ném nếu không chặn
        var result = ChatRequestValidator.Validate(B(json));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Fact]
    public void Validate_WhenModelMissing_ReturnsMissingModel()
    {
        var result = ChatRequestValidator.Validate(B("""{"messages":[{"role":"user"}]}"""));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Theory]
    [InlineData("""{"model":123,"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"","messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"   ","messages":[{"role":"user"}]}""")]
    public void Validate_WhenModelNotNonEmptyString_ReturnsMissingModel(string json)
    {
        var result = ChatRequestValidator.Validate(B(json));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesMissing_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o"}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesEmptyArray_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o","messages":[]}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesNotArray_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o","messages":"hi"}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenValidBody_ReturnsModelIdAndIsValid()
    {
        var result = ChatRequestValidator.Validate(B(ValidBody));

        Assert.True(result.IsValid);
        Assert.Equal(ValidationFailure.None, result.Failure);
        Assert.Equal("gpt-4o-mini", result.ModelId);
    }
}
