using StackExchange.Redis;

namespace UrlShortener.Api.Services;

public record RateLimitResult(bool Allowed, long TokensRemaining, long RetryAfterMs);

public class RateLimiter
{
    private readonly IConnectionMultiplexer _redis;

    // Lua script runs atomically inside Redis — no races possible mid-execution.
    // Inputs:  KEYS[1]=bucket key, ARGV[1]=capacity, ARGV[2]=refill_per_sec,
    //          ARGV[3]=now_ms, ARGV[4]=cost
    // Returns: { allowed (1/0), tokens_remaining_floor, retry_after_ms }
    private const string TokenBucketScript = @"
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill_rate = tonumber(ARGV[2])
        local now_ms = tonumber(ARGV[3])
        local cost = tonumber(ARGV[4])

        local data = redis.call('HMGET', key, 'tokens', 'last_refill_ms')
        local tokens = tonumber(data[1])
        local last_refill_ms = tonumber(data[2])

        if tokens == nil then
            tokens = capacity
            last_refill_ms = now_ms
        end

        local elapsed_sec = (now_ms - last_refill_ms) / 1000.0
        tokens = math.min(capacity, tokens + elapsed_sec * refill_rate)
        last_refill_ms = now_ms

        local allowed = 0
        local retry_after_ms = 0
        if tokens >= cost then
            tokens = tokens - cost
            allowed = 1
        else
            local deficit = cost - tokens
            retry_after_ms = math.ceil(deficit / refill_rate * 1000)
        end

        local ttl_sec = math.ceil(capacity / refill_rate * 2)
        redis.call('HMSET', key, 'tokens', tokens, 'last_refill_ms', last_refill_ms)
        redis.call('EXPIRE', key, ttl_sec)

        return { allowed, math.floor(tokens), retry_after_ms }
    ";

    public RateLimiter(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<RateLimitResult> CheckAsync(
        string key, int capacity, int refillPerSecond, int cost = 1)
    {
        var db = _redis.GetDatabase();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var raw = await db.ScriptEvaluateAsync(
            TokenBucketScript,
            keys: new RedisKey[] { key },
            values: new RedisValue[] { capacity, refillPerSecond, nowMs, cost });

        var result = (RedisValue[])raw!;
        return new RateLimitResult(
            Allowed: (long)result[0] == 1,
            TokensRemaining: (long)result[1],
            RetryAfterMs: (long)result[2]);
    }
}
