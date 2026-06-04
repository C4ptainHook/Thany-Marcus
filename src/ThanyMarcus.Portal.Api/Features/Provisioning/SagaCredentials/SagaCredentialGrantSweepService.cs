using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public sealed class SagaCredentialGrantSweepService(IServiceProvider sp, IClock clock) : BackgroundService
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
        var now = clock.GetCurrentInstant();

        var grantCloudIds = await db.SagaCredentialGrants.Select(g => g.CloudId).ToListAsync(ct);
        if (grantCloudIds.Count == 0)
            return;

        var terminalCloudIds = await db.Clouds
            .IgnoreQueryFilters()
            .Where(c => grantCloudIds.Contains(c.Id) && SagaStatus.Terminal.Contains(c.ProvisioningStatus))
            .Select(c => c.Id)
            .ToListAsync(ct);

        await db.SagaCredentialGrants
            .Where(g => g.ExpiresAt <= now || terminalCloudIds.Contains(g.CloudId))
            .ExecuteDeleteAsync(ct);
    }
}
