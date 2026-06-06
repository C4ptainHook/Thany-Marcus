using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;

public static class CloudCallbackEndpoints
{
    private const int EnrollmentTokenLength = 64;

    public static void MapCloudCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{cloudId:guid}/callback", async (
            Guid cloudId,
            CloudCallbackRequest body,
            PortalDbContext db,
            IDataProtectionProvider dpp,
            IClock clock,
            CancellationToken ct) =>
        {
            if (body.CloudId != cloudId)
                return Results.BadRequest(new { error = "cloud_id_mismatch" });
            if (string.IsNullOrEmpty(body.EnrollmentToken)
                || body.EnrollmentToken.Length != EnrollmentTokenLength)
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(body.CloudAdminToken))
                return Results.BadRequest(new { error = "missing_cloud_admin_token" });

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == cloudId, ct);
            if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });

            var job = await db.ProvisioningJobs
                .Where(j => j.CloudId == cloudId && j.Kind == SagaKinds.Create)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (job is null || job.EnrollmentToken is null)
                return Results.NotFound(new { error = "no_create_job" });

            var expected = Encoding.UTF8.GetBytes(job.EnrollmentToken);
            var provided = Encoding.UTF8.GetBytes(body.EnrollmentToken);
            if (expected.Length != provided.Length
                || !CryptographicOperations.FixedTimeEquals(expected, provided))
                return Results.Unauthorized();

            if (job.Status is SagaStatus.AwaitingCert or SagaStatus.Succeeded)
            {
                await tx.CommitAsync(ct);
                return Results.Ok(new CloudCallbackOkResponse(Idempotent: true));
            }

            if (job.Status != SagaStatus.AwaitingCloudCallback)
                return Results.Conflict(new { error = "wrong_state", current = job.Status });

            var now = clock.GetCurrentInstant();
            var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
            job.AdminTokenCiphertext =
                protector.Protect(Encoding.UTF8.GetBytes(body.CloudAdminToken));
            cloud.AdminStartedAt = now;
            job.Status = SagaStatus.AwaitingCert;
            job.NextVisibleAt = now;
            AppendEventsLog(job, "cloud_registered", now);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('provisioning_job_changed', {0})",
                [job.Id.ToString()], ct);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('provisioning_new', {0})",
                [job.Id.ToString()], ct);

            return Results.Ok(new CloudCallbackOkResponse(Ok: true));
        })
        .WithName("PostCloudCallback")
        .Produces<CloudCallbackOkResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .AllowAnonymous()
        .RequireRateLimiting(CloudCallbackPolicies.PerCloudId)
        .RequireRateLimiting(CloudCallbackPolicies.PerIp);
    }

    private static void AppendEventsLog(ProvisioningJob job, string phase, Instant at)
    {
        var node = JsonNode.Parse(job.EventsLog.RootElement.GetRawText());
        var arr = node as JsonArray ?? [];
        arr.Add(new JsonObject
        {
            ["phase"] = phase,
            ["event"] = phase,
            ["timestamp"] = at.ToString(),
        });
        var previous = job.EventsLog;
        job.EventsLog = JsonDocument.Parse(arr.ToJsonString());
        previous.Dispose();
    }
}
