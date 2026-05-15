using Microsoft.Extensions.Hosting;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class InfraOpUnlockSweepService(InProcessInfraOpUnlockCache cache) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                cache.SweepExpired();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
