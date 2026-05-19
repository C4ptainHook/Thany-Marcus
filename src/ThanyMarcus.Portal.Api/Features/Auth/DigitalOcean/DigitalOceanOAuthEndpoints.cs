using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public static class DigitalOceanOAuthEndpoints
{
    private const string DefaultRegion = "nyc3";

    public static void MapDigitalOceanOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/digitalocean/start", (
            HttpContext http,
            ClaimsPrincipal user,
            DigitalOceanOAuthStateCookie stateCookie,
            IOptions<DigitalOceanOAuthOptions> options,
            IClock clock,
            string? region) =>
        {
            var userIdClaim = user.FindFirstValue(AuthClaimTypes.SubUs);
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
                return Results.Unauthorized();

            var chosenRegion = !string.IsNullOrWhiteSpace(region) && DigitalOceanRegions.IsAllowed(region)
                ? region
                : DefaultRegion;

            var stateNonce = DigitalOceanOAuthStateCookie.GenerateState();
            var state = new DigitalOceanOAuthState(
                State: stateNonce,
                UserId: userId,
                Region: chosenRegion,
                IssuedAtUnixSeconds: clock.GetCurrentInstant().ToUnixTimeSeconds());

            stateCookie.Write(http.Response, stateCookie.Protect(state));

            var opts = options.Value;
            var qs   = HttpUtility.ParseQueryString(string.Empty);
            qs["response_type"] = "code";
            qs["client_id"]     = opts.ClientId;
            qs["redirect_uri"]  = opts.CallbackUrl;
            qs["scope"]         = "read write";
            qs["state"]         = stateNonce;

            var redirect = $"{opts.AuthorizeEndpoint}?{qs}";
            return Results.Redirect(redirect);
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapGet("/oauth/digitalocean/callback", async (
            HttpContext http,
            ClaimsPrincipal user,
            DigitalOceanOAuthStateCookie stateCookie,
            IDigitalOceanOAuthClient doClient,
            IInfraOpUnlockCache unlockCache,
            ICloudSecretBundle secrets,
            PortalDbContext db,
            EnqueueGuard guard,
            HostnameGenerator hostnameGen,
            EnrollmentTokenGenerator tokenGen,
            IClock clock,
            string? code,
            string? state,
            string? error,
            CancellationToken ct) =>
        {
            stateCookie.Clear(http.Response);

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"/clouds/new?error={Uri.EscapeDataString(error)}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.BadRequest(new { error = "missing_code_or_state" });

            if (!http.Request.Cookies.TryGetValue(DigitalOceanOAuthStateCookie.CookieName, out var rawCookie))
                return Results.BadRequest(new { error = "missing_state_cookie" });

            var stored = stateCookie.Unprotect(rawCookie);
            if (stored is null)
                return Results.BadRequest(new { error = "invalid_state_cookie" });

            var nowInstant = clock.GetCurrentInstant();
            if (!stateCookie.IsFresh(stored, nowInstant.ToUnixTimeSeconds()))
                return Results.BadRequest(new { error = "state_expired" });

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(stored.State),
                    System.Text.Encoding.UTF8.GetBytes(state)))
                return Results.BadRequest(new { error = "state_mismatch" });

            var userIdClaim = user.FindFirstValue(AuthClaimTypes.SubUs);
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId) || userId != stored.UserId)
                return Results.Unauthorized();

            var dek = new byte[32];
            try
            {
                if (!await unlockCache.TryGetAsync(userId, dek, ct))
                    return Results.Redirect("/clouds/new?error=step_up_required");

                DoTokenResponse token;
                try
                {
                    token = await doClient.ExchangeCodeAsync(code, ct);
                }
                catch (DigitalOceanOAuthException)
                {
                    return Results.Redirect("/clouds/new?error=oauth_failed");
                }

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

                var now = clock.GetCurrentInstant();
                var cloud = new Cloud
                {
                    UserId             = userId,
                    Name               = hostname,
                    Provider           = KnownDoProvider,
                    Region             = stored.Region,
                    Hostname           = hostname,
                    ProvisioningStatus = SagaStatus.MintingSpaces,
                    ConnectionStatus   = "connected",
                    CreatedAt          = now,
                    UpdatedAt          = now,
                };
                db.Clouds.Add(cloud);
                await db.SaveChangesAsync(ct);

                var accessExpiresAt = now.Plus(Duration.FromSeconds(token.ExpiresIn));
                await secrets.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,  token.AccessToken,  dek, accessExpiresAt, ct);
                await secrets.PutAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, token.RefreshToken, dek, null,            ct);

                var job = new ProvisioningJob
                {
                    CloudId         = cloud.Id,
                    UserId          = userId,
                    Kind            = SagaKinds.Create,
                    Status          = SagaStatus.MintingSpaces,
                    EnrollmentToken = tokenGen.Generate(),
                    NextVisibleAt   = now,
                    Payload         = JsonDocument.Parse("""{"reason":"oauth_callback"}"""),
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

                return Results.Redirect($"/clouds/{cloud.Id}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);
    }

    private const string KnownDoProvider = "digitalocean";
}
