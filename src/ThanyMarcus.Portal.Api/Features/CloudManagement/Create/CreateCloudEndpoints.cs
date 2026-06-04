using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;

public static class CreateCloudEndpoints
{
    public static void MapCreateCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds", async (
            CreateCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            EnqueueGuard guard,
            HostnameGenerator hostnameGen,
            EnrollmentTokenGenerator tokenGen,
            IProvisioningProviderRegistry providers,
            ISagaCredentialSource credentials,
            IDigitalOceanOAuthConnections doConnections,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            if (!providers.TryGet(body.Provider, out var provider) || !provider.UserCreatable)
                return Results.BadRequest(new
                {
                    error = "unsupported_provider",
                    supported = providers.All.Where(p => p.UserCreatable).Select(p => p.Key),
                });
            if (!DigitalOceanRegions.IsAllowed(body.Region))
                return Results.BadRequest(new { error = "invalid_region", region = body.Region });

            if (provider.RequiresCredentials && !await doConnections.IsConnectedAsync(userId, ct))
                return Results.Json(
                    new { error = "connect_required", provider = body.Provider, start = "/oauth/digitalocean/start" },
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

            var initialStatus = provider.InitialStatusAfterCreate;

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

            await credentials.CaptureForSagaAsync(cloud, ct);

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
