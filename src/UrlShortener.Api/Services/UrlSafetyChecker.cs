using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace UrlShortener.Api.Services;

/// <summary>
/// Checks URLs against the URLhaus database (https://urlhaus.abuse.ch/) for known
/// malware / phishing. Results cached in Redis for 6h to avoid hammering URLhaus.
///
/// Fail-open: if URLhaus is down or slow, we allow the URL through and log the issue.
/// Better UX than blocking legit users when a third-party service hiccups.
/// </summary>
public class UrlSafetyChecker
{
    private readonly HttpClient _http;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<UrlSafetyChecker> _log;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private const string UrlhausEndpoint = "https://urlhaus-api.abuse.ch/v1/url/";

    private readonly string? _authKey;

    public UrlSafetyChecker(
        HttpClient http,
        IConnectionMultiplexer redis,
        IConfiguration config,
        ILogger<UrlSafetyChecker> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(3);  // don't block POST /shorten on a slow URLhaus
        _redis = redis;
        _log = log;
        _authKey = config["UrlHaus:AuthKey"];

        if (string.IsNullOrEmpty(_authKey))
        {
            _log.LogWarning(
                "UrlHaus:AuthKey is not configured — URLhaus calls will return 401 and we'll fail-open. " +
                "Register a free key at https://auth.abuse.ch and set UrlHaus__AuthKey env var to enable.");
        }
    }

    public async Task<SafetyResult> CheckAsync(string url)
    {
        var cacheKey = $"safety:{HashUrl(url)}";
        var cache = _redis.GetDatabase();

        // 1. Cached result?
        var cached = await cache.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            return cached == "safe"
                ? SafetyResult.Safe
                : SafetyResult.Malicious(cached.ToString().Replace("unsafe:", ""));
        }

        // 2. Ask URLhaus
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("url", url)
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, UrlhausEndpoint)
            {
                Content = form
            };
            if (!string.IsNullOrEmpty(_authKey))
            {
                request.Headers.Add("Auth-Key", _authKey);
            }

            using var resp = await _http.SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("URLhaus returned {Status}, allowing URL through", resp.StatusCode);
                return SafetyResult.Safe;  // fail-open
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var queryStatus = doc.RootElement.TryGetProperty("query_status", out var qs)
                ? qs.GetString()
                : null;

            // "ok" = URL is in the URLhaus database (known malicious).
            // "no_results" = not in database (treat as safe / unknown).
            if (queryStatus == "ok")
            {
                var threat = doc.RootElement.TryGetProperty("threat", out var t)
                    ? t.GetString() ?? "unknown threat"
                    : "unknown threat";

                await cache.StringSetAsync(cacheKey, $"unsafe:{threat}", CacheTtl);
                return SafetyResult.Malicious(threat);
            }

            await cache.StringSetAsync(cacheKey, "safe", CacheTtl);
            return SafetyResult.Safe;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "URLhaus check failed for {Url}, fail-open", url);
            return SafetyResult.Safe;  // fail-open
        }
    }

    private static string HashUrl(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16];  // 16 hex chars = 64 bits, plenty for cache keys
    }
}

public readonly record struct SafetyResult(bool IsSafe, string? ThreatType)
{
    public static SafetyResult Safe => new(true, null);
    public static SafetyResult Malicious(string threat) => new(false, threat);
}
