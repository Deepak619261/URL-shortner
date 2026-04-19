using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using UrlShortener.Api.Data;
using Xunit;

namespace UrlShortener.Api.Tests;

/// <summary>
/// Spins up real Postgres + Redis containers, applies migrations, exposes a
/// WebApplicationFactory pointed at them. Shared across all integration tests
/// in the assembly via [CollectionDefinition].
///
/// Cost: ~5-10s startup once. Tests then run against real infrastructure
/// (no mocks of EF Core / Redis), catching bugs that unit tests miss.
/// </summary>
public class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RedisContainer _redis;

    public IntegrationTestFixture()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        _redis = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
    }

    // Explicit interface impls because xUnit's IAsyncLifetime wants Task
    // and WebApplicationFactory's lifecycle uses ValueTask.

    Task IAsyncLifetime.InitializeAsync() => InitializeContainersAndDbAsync();

    Task IAsyncLifetime.DisposeAsync() => StopContainersAsync();

    private async Task InitializeContainersAndDbAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Apply EF migrations against the test Postgres
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    private async Task StopContainersAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                ["Jwt:Secret"] = "test-secret-must-be-at-least-32-characters-long-aaaaaaaa",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:ExpiryHours"] = "1",
            });
        });
    }
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }
