using Microsoft.Extensions.Configuration;
using UrlShortener.Api.Models;
using UrlShortener.Api.Services;
using Xunit;

namespace UrlShortener.Api.Tests;

public class AuthServiceTests
{
    private static AuthService BuildService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-must-be-at-least-32-characters-long-aaaaaaaa",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:ExpiryHours"] = "1",
            })
            .Build();
        return new AuthService(config);
    }

    [Fact]
    public void HashPassword_returns_non_empty_hash()
    {
        var auth = BuildService();
        var hash = auth.HashPassword("password123");
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.NotEqual("password123", hash);  // never store plaintext
    }

    [Fact]
    public void HashPassword_same_input_produces_different_hashes()
    {
        // BCrypt salts each hash differently → same password → different hash strings
        var auth = BuildService();
        var h1 = auth.HashPassword("password123");
        var h2 = auth.HashPassword("password123");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void VerifyPassword_returns_true_for_correct_password()
    {
        var auth = BuildService();
        var hash = auth.HashPassword("password123");
        Assert.True(auth.VerifyPassword("password123", hash));
    }

    [Fact]
    public void VerifyPassword_returns_false_for_wrong_password()
    {
        var auth = BuildService();
        var hash = auth.HashPassword("password123");
        Assert.False(auth.VerifyPassword("wrongpassword", hash));
    }

    [Fact]
    public void GenerateToken_returns_non_empty_jwt()
    {
        var auth = BuildService();
        var user = new User { Id = 42, Username = "alice", Email = "alice@x.com" };
        var token = auth.GenerateToken(user);
        Assert.False(string.IsNullOrEmpty(token));
        // JWT structure: header.payload.signature
        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void Constructor_throws_if_secret_too_short()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "short",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new AuthService(config));
    }

    [Fact]
    public void Constructor_throws_if_secret_missing()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new AuthService(config));
    }
}
