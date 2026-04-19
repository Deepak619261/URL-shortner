using StackExchange.Redis;

namespace UrlShortener.Api.Services;

/// <summary>
/// Publishes URL invalidation events to a Redis pub/sub channel.
///
/// Today (single Redis instance), the actual cache key is dropped via DEL right after
/// publishing — this event is "informational" / for additional subscribers (analytics,
/// audit, a future in-process cache layer, multi-region replicas).
///
/// In a multi-instance setup with per-process caches, every app server would subscribe
/// and drop its local copy on receiving an event. The pattern is standard cache fanout.
/// </summary>
public class CacheInvalidationBroker
{
    public const string Channel = "urlshortener:invalidations";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CacheInvalidationBroker> _log;

    public CacheInvalidationBroker(IConnectionMultiplexer redis, ILogger<CacheInvalidationBroker> log)
    {
        _redis = redis;
        _log = log;
    }

    public async Task PublishInvalidationAsync(string code)
    {
        var subscriber = _redis.GetSubscriber();
        var receivers = await subscriber.PublishAsync(RedisChannel.Literal(Channel), code);
        _log.LogInformation("Published invalidation for {Code} to {Receivers} subscribers", code, receivers);
    }
}

/// <summary>
/// Subscribes to the invalidation channel on app startup, logs each event.
/// Real-world: this would drop in-process caches, notify CDN, etc.
/// </summary>
public class CacheInvalidationSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CacheInvalidationSubscriber> _log;

    public CacheInvalidationSubscriber(
        IConnectionMultiplexer redis,
        ILogger<CacheInvalidationSubscriber> log)
    {
        _redis = redis;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(CacheInvalidationBroker.Channel),
            (channel, value) =>
            {
                _log.LogInformation("Received invalidation event for code: {Code}", value);
                // Real-world hooks would go here:
                //   - drop in-process cache
                //   - notify CDN to purge edge cache
                //   - publish to analytics for "deleted code" metric
            });

        // Keep the service alive until shutdown
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        // Unsubscribe on shutdown
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(CacheInvalidationBroker.Channel));
    }
}
