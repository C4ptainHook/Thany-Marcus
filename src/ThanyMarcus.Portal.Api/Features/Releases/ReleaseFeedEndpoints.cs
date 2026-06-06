using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Portal.Api.Features.Releases;

public static class ReleaseFeedEndpoints
{
    public static void MapReleaseFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases", async (PortalDbContext db, CancellationToken ct) =>
        {
            var releases = await db.Releases.AsNoTracking().ToListAsync(ct);
            var ordered = releases
                .OrderByDescending(VersionKey)
                .Select(ReleaseMapping.ToDescriptor)
                .ToList();
            return Results.Ok(ordered);
        }).AllowAnonymous();

        app.MapGet("/api/releases/latest", async (PortalDbContext db, CancellationToken ct) =>
        {
            var latest = await LatestAsync(db, ct);
            return latest is null ? Results.NotFound() : Results.Ok(ReleaseMapping.ToDescriptor(latest));
        }).AllowAnonymous();

        app.MapGet("/api/releases/{version}", async (string version, PortalDbContext db, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(r => r.Version == version, ct);
            return release is null ? Results.NotFound() : Results.Ok(ReleaseMapping.ToDescriptor(release));
        }).AllowAnonymous();
    }

    private static async Task<Release?> LatestAsync(PortalDbContext db, CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().ToListAsync(ct);
        return releases
            .Where(r => SemVer.TryParse(r.Version, out _))
            .OrderByDescending(VersionKey)
            .FirstOrDefault();
    }

    private static SemVer VersionKey(Release release) =>
        SemVer.TryParse(release.Version, out var v) ? v : default;
}
