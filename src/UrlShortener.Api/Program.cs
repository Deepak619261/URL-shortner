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

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/health", () => "OK");

const int MaxRetries = 5;

app.MapPost("/shorten", async (
    ShortenRequest req,
    AppDbContext db,
    HttpContext ctx,
    RateLimiter rateLimiter,
    ILogger<Program> log) =>
{
    // 0. Rate limit per IP (token bucket: 100 capacity, 10/sec refill)
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var rl = await rateLimiter.CheckAsync(
        key: $"ratelimit:shorten:{ip}",
        capacity: 100,
        refillPerSecond: 10);
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

    // 2. Generate code + insert, retrying on UNIQUE collision
    for (int attempt = 1; attempt <= MaxRetries; attempt++)
    {
        var code = CodeGenerator.Generate();
        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();

        var entity = new ShortCode
        {
            Code = code,
            LongUrl = req.LongUrl,
            CreatedByIp = clientIp,
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

app.Run();

// DTOs (kept here since we have only a few endpoints)
record ShortenRequest(string LongUrl);
record ShortenResponse(string ShortCode, string ShortUrl);
record AnalyticsResponse(string Code, int TotalClicks, DateTime? LastClickedAt);
