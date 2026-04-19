using UrlShortener.Api.Services;
using Xunit;

namespace UrlShortener.Api.Tests;

public class CodeValidatorTests
{
    [Theory]
    [InlineData("abc")]              // 3 chars, lowercase
    [InlineData("aBc1234")]          // mixed case + digits
    [InlineData("promo2026")]        // realistic example
    [InlineData("A1b2C3D4")]         // alternating
    public void Validate_accepts_valid_codes(string code)
    {
        var (ok, error) = CodeValidator.Validate(code);
        Assert.True(ok, $"Expected '{code}' to be valid, got error: {error}");
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ab", "must be 3-10")]               // too short
    [InlineData("12345678901", "must be 3-10")]      // too long
    [InlineData("abc-def", "letters and digits")]    // contains hyphen
    [InlineData("abc def", "letters and digits")]    // contains space
    [InlineData("abc!", "letters and digits")]       // contains punctuation
    [InlineData("abc.com", "letters and digits")]    // contains dot
    public void Validate_rejects_invalid_format(string code, string errorContains)
    {
        var (ok, error) = CodeValidator.Validate(code);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains(errorContains, error);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("HEALTH")]      // case insensitive
    [InlineData("shorten")]
    [InlineData("analytics")]
    [InlineData("admin")]
    [InlineData("login")]
    public void Validate_rejects_reserved_words(string code)
    {
        var (ok, error) = CodeValidator.Validate(code);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("reserved", error);
    }

    [Fact]
    public void Validate_rejects_empty_string()
    {
        var (ok, error) = CodeValidator.Validate("");
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
