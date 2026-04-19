using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using UrlShortener.Api.Data;
using UrlShortener.Api.Models;
using UrlShortener.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Postgres via EF Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Redis (singleton — connection multiplexer is thread-safe and reused)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

// Click analytics: in-memory queue + background flusher
builder.Services.AddSingleton<ClickQueue>();
builder.Services.AddHostedService<ClickFlushService>();

// Rate limiter (Redis-backed token bucket)
builder.Services.AddSingleton<RateLimiter>();

// URL safety check (URLhaus). HttpClient pooled via factory.
builder.Services.AddHttpClient<UrlSafetyChecker>();

// Cache invalidation pub/sub
builder.Services.AddSingleton<CacheInvalidationBroker>();
builder.Services.AddHostedService<CacheInvalidationSubscriber>();

// JWT auth — construct AuthService inside the lambda so it picks up the FINAL
// merged configuration (including any test-time IConfiguration overrides),
// not the partially-merged configuration available at builder time.
builder.Services.AddSingleton<AuthService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        var auth = new AuthService(builder.Configuration);
        opts.TokenValidationParameters = auth.BuildValidationParams();
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Serve static files from wwwroot/ (the minimal UI lives there)
app.UseDefaultFiles();   // makes / serve index.html
app.UseStaticFiles();    // serves any file under wwwroot/

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => "OK");

// ==== Auth endpoints ====

app.MapPost("/auth/register", async (
    RegisterRequest req,
    AppDbContext db,
    AuthService auth) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3 || req.Username.Length > 50)
        return Results.BadRequest("Username must be 3-50 characters");
    if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
        return Results.BadRequest("Invalid email");
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
        return Results.BadRequest("Password must be at least 8 characters");

    if (await db.Users.AnyAsync(u => u.Username == req.Username))
        return Results.Conflict("Username already taken");
    if (await db.Users.AnyAsync(u => u.Email == req.Email))
        return Results.Conflict("Email already registered");

    var user = new User
    {
        Username = req.Username,
        Email = req.Email,
        PasswordHash = auth.HashPassword(req.Password),
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/auth/users/{user.Id}",
        new AuthResponse(auth.GenerateToken(user), user.Username, user.Email));
});

app.MapPost("/auth/login", async (
    LoginRequest req,
    AppDbContext db,
    AuthService auth) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
    if (user is null || !auth.VerifyPassword(req.Password, user.PasswordHash))
        return Results.Unauthorized();

    return Results.Ok(new AuthResponse(auth.GenerateToken(user), user.Username, user.Email));
});

const int MaxRetries = 5;

app.MapPost("/shorten", async (
    ShortenRequest req,
    AppDbContext db,
    HttpContext ctx,
    RateLimiter rateLimiter,
    UrlSafetyChecker safety,
    ILogger<Program> log) =>
{
    // 0. Rate limit — authenticated users get a HIGHER tier (500/50) keyed by user-id;
    //    anonymous users get the standard tier (100/10) keyed by IP.
    var rateLimitUserId = GetUserId(ctx);
    var (rlKey, rlCapacity, rlRefill) = rateLimitUserId is long uid
        ? ($"ratelimit:shorten:user:{uid}", 500, 50)
        : ($"ratelimit:shorten:ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}", 100, 10);

    var rl = await rateLimiter.CheckAsync(
        key: rlKey,
        capacity: rlCapacity,
        refillPerSecond: rlRefill);
    if (!rl.Allowed)
    {
        var retryAfterSec = Math.Max(1, (int)((rl.RetryAfterMs + 999) / 1000));
        ctx.Response.Headers["Retry-After"] = retryAfterSec.ToString();
        return Results.Problem(
            $"Rate limit exceeded. Retry after {retryAfterSec}s.",
            statusCode: 429);
    }

    // 1. Validate input
    if (string.IsNullOrWhiteSpace(req.LongUrl))
        return Results.BadRequest("longUrl is required");

    if (req.LongUrl.Length > 2048)
        return Results.BadRequest("URL too long (max 2048 chars)");

    if (!Uri.TryCreate(req.LongUrl, UriKind.Absolute, out var parsedUri) ||
        (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
        return Results.BadRequest("Invalid URL — must be http or https");

    // 1b. Malicious URL check (URLhaus). Fail-open if URLhaus is down.
    var safetyResult = await safety.CheckAsync(req.LongUrl);
    if (!safetyResult.IsSafe)
    {
        log.LogWarning("Blocked malicious URL submission: {Url} (threat: {Threat})",
            req.LongUrl, safetyResult.ThreatType);
        return Results.BadRequest($"URL flagged as malicious by URLhaus (threat: {safetyResult.ThreatType})");
    }

    var clientIpForInsert = ctx.Connection.RemoteIpAddress?.ToString();
    var userIdForInsert = GetUserId(ctx);  // null if not authenticated

    // 2a. Custom code path — caller supplied a slug
    if (!string.IsNullOrWhiteSpace(req.CustomCode))
    {
        var (ok, error) = CodeValidator.Validate(req.CustomCode);
        if (!ok) return Results.BadRequest(error);

        var entity = new ShortCode
        {
            Code = req.CustomCode,
            LongUrl = req.LongUrl,
            CreatedByIp = clientIpForInsert,
            UserId = userIdForInsert,
        };
        db.ShortCodes.Add(entity);
        try
        {
            await db.SaveChangesAsync();
            var shortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{req.CustomCode}";
            return Results.Created($"/{req.CustomCode}", new ShortenResponse(req.CustomCode, shortUrl));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.Entry(entity).State = EntityState.Detached;
            return Results.Conflict($"Custom code '{req.CustomCode}' is already taken");
        }
    }

    // 2b. Random code path — generate + insert, retrying on UNIQUE collision
    for (int attempt = 1; attempt <= MaxRetries; attempt++)
    {
        var code = CodeGenerator.Generate();
        var clientIp = clientIpForInsert;

        var entity = new ShortCode
        {
            Code = code,
            LongUrl = req.LongUrl,
            CreatedByIp = clientIp,
            UserId = userIdForInsert,
        };

        db.ShortCodes.Add(entity);

        try
        {
            await db.SaveChangesAsync();

            var shortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{code}";
            return Results.Created($"/{code}", new ShortenResponse(code, shortUrl));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Detach the failed entity so next iteration's Add doesn't conflict
            db.Entry(entity).State = EntityState.Detached;
            log.LogWarning("Code collision on attempt {Attempt} for code {Code}, retrying", attempt, code);
        }
    }

    log.LogError("Failed to generate a unique code after {MaxRetries} attempts", MaxRetries);
    return Results.Problem(
        "Could not generate a unique code. Please try again.",
        statusCode: 500);
});

app.MapGet("/{code}", async (
    string code,
    AppDbContext db,
    IConnectionMultiplexer redis,
    ClickQueue clickQueue,
    HttpContext ctx,
    ILogger<Program> log) =>
{
    var cacheKey = $"url:{code}";
    var cache = redis.GetDatabase();

    string? longUrl = null;

    // 1. Check Redis (cache-aside read)
    var cached = await cache.StringGetAsync(cacheKey);
    if (cached.HasValue)
    {
        log.LogInformation("Cache HIT for {Code}", code);
        longUrl = cached!;
    }
    else
    {
        // 2. Cache MISS — fall back to Postgres
        log.LogInformation("Cache MISS for {Code}, querying DB", code);
        var entry = await db.ShortCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == code);

        if (entry is null)
            return Results.NotFound();

        longUrl = entry.LongUrl;

        // 3. Populate Redis with TTL=1h so future reads hit cache
        await cache.StringSetAsync(cacheKey, longUrl, TimeSpan.FromHours(1));
    }

    // 4. Enqueue click event (fire-and-forget — does NOT block the redirect)
    await clickQueue.EnqueueAsync(new ClickEvent(
        Code: code,
        ClickedAt: DateTime.UtcNow,
        Ip: ctx.Connection.RemoteIpAddress?.ToString(),
        UserAgent: ctx.Request.Headers.UserAgent.ToString()));

    return Results.Redirect(longUrl, permanent: false);
});

app.MapGet("/analytics/{code}", async (string code, AppDbContext db) =>
{
    var exists = await db.ShortCodes.AsNoTracking().AnyAsync(s => s.Code == code);
    if (!exists) return Results.NotFound();

    var totalClicks = await db.Clicks.AsNoTracking().CountAsync(c => c.Code == code);

    DateTime? lastClickedAt = null;
    if (totalClicks > 0)
    {
        lastClickedAt = await db.Clicks
            .AsNoTracking()
            .Where(c => c.Code == code)
            .MaxAsync(c => c.ClickedAt);
    }

    return Results.Ok(new AnalyticsResponse(code, totalClicks, lastClickedAt));
});

// DELETE removes the mapping AND invalidates the cache entry.
// Requires JWT auth and the caller must be the owner of the code.
app.MapDelete("/{code}", async (
    string code,
    AppDbContext db,
    IConnectionMultiplexer redis,
    CacheInvalidationBroker broker,
    HttpContext ctx,
    ILogger<Program> log) =>
{
    var userId = GetUserId(ctx);
    if (userId is null) return Results.Unauthorized();

    var entry = await db.ShortCodes.FirstOrDefaultAsync(s => s.Code == code);
    if (entry is null) return Results.NotFound();

    if (entry.UserId != userId)
        return Results.Forbid();   // 403 — authenticated but not the owner

    db.ShortCodes.Remove(entry);
    await db.SaveChangesAsync();

    var removed = await redis.GetDatabase().KeyDeleteAsync($"url:{code}");
    log.LogInformation("User {UserId} deleted code {Code}, cache invalidated: {Removed}",
        userId, code, removed);

    // Broadcast invalidation to any other subscribers (multi-region, in-process caches, audit, etc.)
    await broker.PublishInvalidationAsync(code);

    return Results.NoContent();   // HTTP 204
}).RequireAuthorization();

app.Run();

// Helper: extracts the user ID from the JWT 'sub' claim, or null if anonymous
static long? GetUserId(HttpContext ctx)
{
    var sub = ctx.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
              ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return long.TryParse(sub, out var id) ? id : null;
}

// DTOs (kept here since we have only a few endpoints)
record ShortenRequest(string LongUrl, string? CustomCode = null);
record ShortenResponse(string ShortCode, string ShortUrl);
record AnalyticsResponse(string Code, int TotalClicks, DateTime? LastClickedAt);
record RegisterRequest(string Username, string Email, string Password);
record LoginRequest(string Username, string Password);
record AuthResponse(string Token, string Username, string Email);

// Required so WebApplicationFactory<Program> in integration tests can find this entry point
public partial class Program { }
