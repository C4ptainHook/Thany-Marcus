using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;

public static class CreateCloudEndpoints
{
    private static readonly string[] SupportedProviders = ["digitalocean"];

    public static void MapCreateCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds", async (
            CreateCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            EnqueueGuard guard,
            HostnameGenerator hostnameGen,
            EnrollmentTokenGenerator tokenGen,
            IDigitalOceanOAuthConnections doConnections,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            if (body.Provider != "digitalocean")
                return Results.BadRequest(new { error = "unsupported_provider", supported = SupportedProviders });
            if (!DigitalOceanRegions.IsAllowed(body.Region))
                return Results.BadRequest(new { error = "invalid_region", region = body.Region });

            if (!await doConnections.IsConnectedAsync(userId, ct))
                return Results.Json(
                    new { error = "connect_required", provider = "digitalocean", start = "/oauth/digitalocean/start" },
                    statusCode: StatusCodes.Status412PreconditionFailed);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var inFlight = await guard.CheckUserCreateInFlightAsync(userId, ct);
            if (inFlight is { } existing)
                return Results.Conflict(new { error = "user_create_in_flight", in_flight_job_id = existing.JobId });

            string hostname;
            try
            {
                hostname = await hostnameGen.GenerateAsync(ct);
            }
            catch (InvalidOperationException ex) when (ex.Message == "hostname_generation_exhausted")
            {
                return Results.Problem(
                    title: "hostname_generation_exhausted",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var enrollmentToken = tokenGen.Generate();
            var now = clock.GetCurrentInstant();

            var initialStatus = body.Provider == "digitalocean"
                ? SagaStatus.MintingSpaces
                : SagaStatus.TfPlanning;

            var cloud = new Cloud
            {
                UserId             = userId,
                Name               = hostname,
                Provider           = body.Provider,
                Region             = body.Region,
                Hostname           = hostname,
                ProvisioningStatus = initialStatus,
                CreatedAt          = now,
                UpdatedAt          = now,
            };
            db.Clouds.Add(cloud);
            await db.SaveChangesAsync(ct);

            var job = new ProvisioningJob
            {
                CloudId         = cloud.Id,
                UserId          = userId,
                Kind            = SagaKinds.Create,
                Status          = initialStatus,
                EnrollmentToken = enrollmentToken,
                NextVisibleAt   = now,
                Payload         = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                EventsLog       = JsonDocument.Parse("[]"),
                CreatedAt       = now,
                UpdatedAt       = now,
            };
            db.ProvisioningJobs.Add(job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify('provisioning_new', {0})",
                [job.Id.ToString()], ct);

            return Results.AcceptedAtRoute(
                "GetCloudStatus",
                new { id = cloud.Id },
                new CreateCloudResponse(cloud.Id, job.Id, cloud.Hostname));
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
