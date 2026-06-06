using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Portal.Api.Features.Releases;

public static class ReleaseRegistrationEndpoint
{
    public static void MapReleaseRegistrationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/releases", async (
            RegisterReleaseRequest body,
            HttpContext http,
            PortalDbContext db,
            IOptions<ReleaseFeedOptions> options,
            IClock clock,
            CancellationToken ct) =>
        {
            var configured = options.Value.RegistrationToken;
            if (string.IsNullOrEmpty(configured))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            if (!TryReadBearer(http, out var presented) || !FixedTimeEquals(presented, configured))
                return Results.Unauthorized();

            if (!SemVer.TryParse(body.Version, out _))
                return Results.BadRequest(new { error = "invalid_version" });
            if (!ReleaseStrategies.IsValid(body.Strategy))
                return Results.BadRequest(new { error = "invalid_strategy" });
            if (string.IsNullOrWhiteSpace(body.ComposeYaml))
                return Results.BadRequest(new { error = "missing_compose" });

            var schemaMinFrom = string.IsNullOrWhiteSpace(body.SchemaMinFrom) ? body.Version : body.SchemaMinFrom;
            if (!SemVer.TryParse(schemaMinFrom, out _))
                return Results.BadRequest(new { error = "invalid_schema_min_from" });

            var now = clock.GetCurrentInstant();
            var release = await db.Releases.SingleOrDefaultAsync(r => r.Version == body.Version, ct);
            if (release is null)
            {
                release = new Release { Version = body.Version, CreatedAt = now, UpdatedAt = now };
                db.Releases.Add(release);
            }

            release.Strategy = body.Strategy;
            release.ComposeYaml = body.ComposeYaml;
            release.ImageDigests = ReleaseMapping.ToJsonDocument(body.ImageDigests);
            release.ModelTags = ReleaseMapping.ToJsonDocument(body.ModelTags);
            release.EnvOverlay = ReleaseMapping.ToJsonDocument(body.EnvOverlay);
            release.SchemaMinFrom = schemaMinFrom;
            release.Notes = body.Notes;
            release.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            return Results.Ok(ReleaseMapping.ToDescriptor(release));
        });
    }

    private static bool TryReadBearer(HttpContext http, out string token)
    {
        token = "";
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
