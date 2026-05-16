using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class EnqueueGuard(PortalDbContext db)
{
    public async Task<InFlightConflict?> CheckAsync(Guid cloudId, CancellationToken ct)
    {
        return await db.ProvisioningJobs
            .Where(j => j.CloudId == cloudId && !SagaStatus.Terminal.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new InFlightConflict(j.Id, j.Status))
            .FirstOrDefaultAsync(ct);
    }
}

public sealed record InFlightConflict(Guid JobId, string CurrentPhase);
