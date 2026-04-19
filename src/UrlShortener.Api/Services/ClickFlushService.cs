using UrlShortener.Api.Data;
using UrlShortener.Api.Models;

namespace UrlShortener.Api.Services;

public class ClickFlushService : BackgroundService
{
    private readonly ClickQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClickFlushService> _log;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    public ClickFlushService(
        ClickQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ClickFlushService> log)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await DrainAndFlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown — fall through to final drain below
        }

        // Final drain on shutdown so we don't lose unflushed clicks
        await DrainAndFlushAsync(CancellationToken.None);
    }

    private async Task DrainAndFlushAsync(CancellationToken ct)
    {
        var batch = new List<ClickEvent>();
        while (_queue.Reader.TryRead(out var click))
        {
            batch.Add(click);
        }

        if (batch.Count == 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var clicks = batch.Select(b => new Click
            {
                Code = b.Code,
                ClickedAt = b.ClickedAt,
                Ip = b.Ip,
                UserAgent = b.UserAgent,
            }).ToList();

            db.Clicks.AddRange(clicks);
            await db.SaveChangesAsync(ct);

            _log.LogInformation("Flushed {Count} clicks to DB", clicks.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to flush {Count} clicks", batch.Count);
        }
    }
}
