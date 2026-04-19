using UrlShortener.Api.Services;
using Xunit;

namespace UrlShortener.Api.Tests;

public class CodeGeneratorTests
{
    [Fact]
    public void Generate_default_returns_7_char_string()
    {
        var code = CodeGenerator.Generate();
        Assert.Equal(7, code.Length);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public void Generate_returns_string_of_requested_length(int length)
    {
        var code = CodeGenerator.Generate(length);
        Assert.Equal(length, code.Length);
    }

    [Fact]
    public void Generate_only_uses_base62_alphabet()
    {
        var code = CodeGenerator.Generate(50);
        foreach (var c in code)
        {
            bool isBase62 =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z');
            Assert.True(isBase62, $"Character '{c}' is not in base62 alphabet");
        }
    }

    [Fact]
    public void Generate_two_calls_return_different_codes()
    {
        // Not a strict guarantee but with 62^7 = 3.5T space, two consecutive
        // calls colliding is astronomically unlikely. If this ever fails,
        // CSPRNG is broken.
        var a = CodeGenerator.Generate();
        var b = CodeGenerator.Generate();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Generate_1000_calls_produce_no_collisions()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var code = CodeGenerator.Generate();
            Assert.True(seen.Add(code), $"Duplicate code generated: {code}");
        }
    }
}
