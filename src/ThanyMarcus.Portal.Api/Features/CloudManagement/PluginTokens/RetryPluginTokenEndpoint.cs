using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens;

public static class RetryPluginTokenEndpoint
{
    public static void MapRetryPluginTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{cloudId:guid}/plugin-tokens/retry", async (
            Guid cloudId,
            ClaimsPrincipal user,
            PortalDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == cloudId, ct);

            if (cloud is null)
                return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId)
                return Results.Forbid();
            if (cloud.ProvisioningStatus != SagaStatus.FailedPluginToken)
                return Results.Conflict(new
                {
                    error = "not_in_failed_plugin_token",
                    currentStatus = cloud.ProvisioningStatus,
                });

            var job = await db.ProvisioningJobs
                .Where(j => j.CloudId == cloudId
                         && j.Kind == SagaKinds.Create
                         && j.Status == SagaStatus.FailedPluginToken)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (job is null)
                return Results.Conflict(new { error = "no_failed_plugin_token_job" });

            var now = clock.GetCurrentInstant();
            job.Status           = SagaStatus.IssuingPluginToken;
            job.AttemptCount     = 0;
            job.LastError        = null;
            job.ClaimedBy        = null;
            job.LeaseExpiresAt   = null;
            job.NextVisibleAt    = now;
            job.PhaseStartedAt   = now;
            job.TransitionVersion += 1;

            cloud.ProvisioningStatus = SagaStatus.IssuingPluginToken;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('provisioning_job_changed', {0})",
                [job.Id.ToString()], ct);

            return Results.Accepted(
                $"/api/clouds/{cloud.Id}/status",
                new RetryPluginTokenResponse(job.Id, SagaStatus.IssuingPluginToken));
        })
        .WithName("PostCloudPluginTokensRetry")
        .Produces<RetryPluginTokenResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAuthorization();
    }
}

public sealed record RetryPluginTokenResponse(Guid JobId, string Status);
