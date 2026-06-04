using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Destroy;

public static class DestroyCloudEndpoints
{
    private static readonly HashSet<string> DestroyableProvisioningStatuses =
        new(StringComparer.Ordinal)
        {
            SagaStatus.Succeeded,
            SagaStatus.AwaitingCert,
            SagaStatus.FailedCert,
            SagaStatus.FailedPluginToken,
            SagaStatus.FailedDestroy,
        };

    public static void MapDestroyCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{id:guid}/destroy", async (
            Guid id,
            DestroyCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            EnqueueGuard guard,
            ISagaCredentialSource credentials,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Clouds
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);

            if (cloud is null)
                return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId)
                return Results.Forbid();
            if (cloud.DestroyedAt is not null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (!DestroyableProvisioningStatuses.Contains(cloud.ProvisioningStatus))
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (!string.Equals(cloud.Hostname, body.ConfirmHostname, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "hostname_mismatch", expected = cloud.Hostname });

            var conflict = await guard.CheckAsync(cloud.Id, ct);
            if (conflict is { } c)
                return Results.Conflict(new
                {
                    reason = "cloud_busy",
                    inFlightJobId = c.JobId,
                    currentPhase = c.CurrentPhase,
                });

            var now = clock.GetCurrentInstant();
            var job = new ProvisioningJob
            {
                CloudId       = cloud.Id,
                UserId        = userId,
                Kind          = SagaKinds.Destroy,
                Payload       = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                Status        = SagaStatus.Destroying,
                NextVisibleAt = now,
                EventsLog     = JsonDocument.Parse("[]"),
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            db.ProvisioningJobs.Add(job);

            cloud.ProvisioningStatus = SagaStatus.Destroying;
            cloud.UpdatedAt = now;

            await credentials.CaptureForSagaAsync(cloud, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Accepted(
                $"/api/clouds/{cloud.Id}/status",
                new { jobId = job.Id, cloudId = cloud.Id });
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
