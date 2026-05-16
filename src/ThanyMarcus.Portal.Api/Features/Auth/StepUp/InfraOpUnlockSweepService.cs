using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class InfraOpUnlockSweepService(IServiceProvider sp, IClock clock) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
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
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var cutoff = clock.GetCurrentInstant();
        await db.StepUpUnlocks
            .Where(u => u.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
