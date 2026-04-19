using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UrlShortener.Api.Tests;

[Collection("Integration")]
public class EndToEndTests
{
    private readonly IntegrationTestFixture _fixture;

    public EndToEndTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private HttpClient NewClient() => _fixture.CreateClient();

    [Fact]
    public async Task Health_endpoint_returns_OK()
    {
        var client = NewClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("OK", body);
    }

    [Fact]
    public async Task Post_shorten_returns_201_with_short_code()
    {
        var client = NewClient();
        var resp = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/e2e-test" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var shortCode = body.GetProperty("shortCode").GetString()!;
        Assert.Equal(7, shortCode.Length);   // default base62 length
    }

    [Fact]
    public async Task Post_shorten_with_invalid_url_returns_400()
    {
        var client = NewClient();
        var resp = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_shorten_with_custom_code_works_and_duplicate_returns_409()
    {
        var client = NewClient();
        var slug = $"e2e{Guid.NewGuid().ToString("N")[..5]}";

        var first = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/slug", customCode = slug });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var dup = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://other.com", customCode = slug });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Get_shortcode_returns_302_redirect()
    {
        var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var create = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/redirect-test" });
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("shortCode").GetString()!;

        var resp = await client.GetAsync($"/{code}");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);  // 302
        Assert.Equal(new Uri("https://example.com/redirect-test"), resp.Headers.Location);
    }

    [Fact]
    public async Task Get_nonexistent_shortcode_returns_404()
    {
        var client = NewClient();
        var resp = await client.GetAsync("/zzzzzzz");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_without_auth_returns_401()
    {
        var client = NewClient();

        // create an anonymous code
        var create = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/delete-anon" });
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("shortCode").GetString()!;

        var del = await client.DeleteAsync($"/{code}");
        Assert.Equal(HttpStatusCode.Unauthorized, del.StatusCode);
    }

    [Fact]
    public async Task Register_login_and_delete_owned_code_end_to_end()
    {
        var client = NewClient();
        var username = $"user{Guid.NewGuid().ToString("N")[..8]}";

        // Register
        var reg = await client.PostAsJsonAsync("/auth/register",
            new { username, email = $"{username}@test.com", password = "password123" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var regBody = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var token = regBody.GetProperty("token").GetString()!;

        // Create code as this user
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var slug = $"own{Guid.NewGuid().ToString("N")[..5]}";
        var create = await client.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/owned", customCode = slug });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // Delete as owner — should succeed
        var del = await client.DeleteAsync($"/{slug}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Subsequent GET returns 404
        var get = await client.GetAsync($"/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_by_non_owner_returns_403()
    {
        var ownerClient = NewClient();
        var ownerName = $"ownA{Guid.NewGuid().ToString("N")[..6]}";

        // register owner
        var regOwner = await ownerClient.PostAsJsonAsync("/auth/register",
            new { username = ownerName, email = $"{ownerName}@test.com", password = "password123" });
        var ownerToken = (await regOwner.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var slug = $"frb{Guid.NewGuid().ToString("N")[..5]}";
        await ownerClient.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/private", customCode = slug });

        // register OTHER user
        var otherClient = NewClient();
        var otherName = $"other{Guid.NewGuid().ToString("N")[..6]}";
        var regOther = await otherClient.PostAsJsonAsync("/auth/register",
            new { username = otherName, email = $"{otherName}@test.com", password = "password123" });
        var otherToken = (await regOther.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);

        // Other tries to delete owner's code
        var del = await otherClient.DeleteAsync($"/{slug}");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var client = NewClient();
        var username = $"wpw{Guid.NewGuid().ToString("N")[..6]}";

        await client.PostAsJsonAsync("/auth/register",
            new { username, email = $"{username}@test.com", password = "password123" });

        var badLogin = await client.PostAsJsonAsync("/auth/login",
            new { username, password = "WRONG" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
    }

    [Fact]
    public async Task My_codes_requires_auth_and_returns_only_callers_codes()
    {
        // Anonymous → 401
        var anon = NewClient();
        var unauth = await anon.GetAsync("/my/codes");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // Register a user, create 3 codes
        var client = NewClient();
        var username = $"my{Guid.NewGuid().ToString("N")[..6]}";
        var reg = await client.PostAsJsonAsync("/auth/register",
            new { username, email = $"{username}@test.com", password = "password123" });
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        for (int i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/shorten",
                new { longUrl = $"https://example.com/mine{i}" });
        }

        // Different user creates 1 code (should NOT show in our list)
        var otherClient = NewClient();
        var otherName = $"oth{Guid.NewGuid().ToString("N")[..6]}";
        var otherReg = await otherClient.PostAsJsonAsync("/auth/register",
            new { username = otherName, email = $"{otherName}@test.com", password = "password123" });
        var otherToken = (await otherReg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        await otherClient.PostAsJsonAsync("/shorten",
            new { longUrl = "https://example.com/not-mine" });

        // GET /my/codes as the first user
        var resp = await client.GetAsync("/my/codes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal(3, body.GetProperty("items").GetArrayLength());
    }
}
