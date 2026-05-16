using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Lockout;

public sealed partial class AuthLockoutSweepService(
    IServiceScopeFactory scopeFactory,
    IOptions<LockoutOptions> options,
    ILogger<AuthLockoutSweepService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Sweep.IntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(options.Value.Sweep.RetentionDays);
        var deleted = await db.AuthLockouts
            .Where(a => a.LastAttemptAt < cutoff
                     && (a.LockedUntil == null || a.LockedUntil < now))
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            LogSwept(logger, deleted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AuthLockoutSweep deleted {Count} stale lockout rows")]
    private static partial void LogSwept(ILogger logger, int count);
}
