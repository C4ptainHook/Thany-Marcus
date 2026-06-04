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

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Cancel;

public static class CancelCloudEndpoints
{
    private static readonly HashSet<string> CancellableProvisioningStatuses =
        new(StringComparer.Ordinal)
        {
            SagaStatus.Pending,
            SagaStatus.TfPlanning,
            SagaStatus.TfApplying,
            SagaStatus.DnsCreating,
            SagaStatus.AwaitingCloudCallback,
            SagaStatus.AwaitingCert,
            SagaStatus.RollingBackTf,
            SagaStatus.RollingBackDns,
            SagaStatus.FailedTf,
            SagaStatus.FailedDns,
            SagaStatus.FailedCallback,
            SagaStatus.FailedCert,
        };

    public static void MapCancelCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{id:guid}/cancel", async (
            Guid id,
            CancelCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
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
            if (cloud.ProvisioningStatus == SagaStatus.Succeeded)
                return Results.Conflict(new { error = "use_destroy_instead" });
            if (cloud.ProvisioningStatus == SagaStatus.Cancelled
             || cloud.ProvisioningStatus == SagaStatus.RolledBack)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (!CancellableProvisioningStatuses.Contains(cloud.ProvisioningStatus))
                return Results.Conflict(new { error = "not_cancellable", currentStatus = cloud.ProvisioningStatus });
            if (!string.Equals(cloud.Hostname, body.ConfirmHostname, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "hostname_mismatch", expected = cloud.Hostname });

            var existingCancel = await db.ProvisioningJobs
                .Where(j => j.CloudId == cloud.Id
                         && j.Kind == SagaKinds.Cancel
                         && !SagaStatus.Terminal.Contains(j.Status))
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (existingCancel is not null)
                return Results.Accepted(
                    $"/api/clouds/{cloud.Id}/status",
                    new { jobId = existingCancel.Id, cloudId = cloud.Id, status = "cancel_in_flight" });

            var now = clock.GetCurrentInstant();
            var job = new ProvisioningJob
            {
                CloudId       = cloud.Id,
                UserId        = userId,
                Kind          = SagaKinds.Cancel,
                Payload       = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                Status        = SagaStatus.Pending,
                NextVisibleAt = now,
                EventsLog     = JsonDocument.Parse("[]"),
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            db.ProvisioningJobs.Add(job);

            cloud.CancelRequestedAt = now;

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

public sealed record CancelCloudRequest(string ConfirmHostname);
