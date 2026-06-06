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
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;

public static class MigrateCloudEndpoints
{
    public static void MapMigrateCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{id:guid}/migrate", async (
            Guid id,
            MigrateCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            ISagaCredentialSource credentials,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            if (!SemVer.TryParse(body.TargetVersion, out _))
                return Results.BadRequest(new { error = "invalid_target_version" });

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Clouds.IgnoreQueryFilters().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cloud is null || cloud.DestroyedAt is not null)
                return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId)
                return Results.Forbid();
            if (cloud.ProvisioningStatus != SagaStatus.Succeeded)
                return Results.Conflict(new { error = "cloud_not_ready", status = cloud.ProvisioningStatus });

            var active = await db.ProvisioningJobs
                .Where(j => j.CloudId == id && j.Kind == SagaKinds.Migrate)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (active is not null && !SagaStatus.IsTerminal(active.Status))
                return Results.Conflict(new { error = "migrate_in_flight", job_id = active.Id });

            var now = clock.GetCurrentInstant();
            await credentials.CaptureForSagaAsync(cloud, ct);

            cloud.ProvisioningStatus = SagaStatus.MigrateQuiescing;
            cloud.UpdatedAt = now;

            var payload = JsonSerializer.SerializeToDocument(new { target_version = body.TargetVersion });
            var job = new ProvisioningJob
            {
                CloudId       = id,
                UserId        = userId,
                Kind          = SagaKinds.Migrate,
                Status        = SagaStatus.MigrateQuiescing,
                NextVisibleAt = now,
                Payload       = payload,
                EventsLog     = JsonDocument.Parse("[]"),
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            db.ProvisioningJobs.Add(job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('provisioning_new', {0})", [job.Id.ToString()], ct);

            return Results.Accepted($"/api/clouds/{id}/status", new MigrateCloudResponse(id, job.Id));
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}

public sealed record MigrateCloudRequest(string TargetVersion);
public sealed record MigrateCloudResponse(Guid CloudId, Guid JobId);
