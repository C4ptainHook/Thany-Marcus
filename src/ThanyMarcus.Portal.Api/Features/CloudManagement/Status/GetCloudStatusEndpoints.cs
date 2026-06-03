using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Status;

public static class GetCloudStatusEndpoints
{
    private const int RecentEventsTake = 10;

    public static void MapGetCloudStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/clouds/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            PortalDbContext db,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId) return Results.Forbid();

            return Results.Ok(await BuildStatusAsync(cloud, db, ct));
        })
        .WithName("GetCloudStatus")
        .RequireAuthorization();

        app.MapGet("/api/clouds/me", async (
            ClaimsPrincipal user,
            PortalDbContext db,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var cloud = await db.Clouds
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cloud is null) return Results.NotFound(new { error = "no_cloud" });

            return Results.Ok(await BuildStatusAsync(cloud, db, ct));
        })
        .WithName("GetMyCloud")
        .RequireAuthorization();
    }

    private static async Task<CloudStatusResponse> BuildStatusAsync(Cloud cloud, PortalDbContext db, CancellationToken ct)
    {
        var job = await db.ProvisioningJobs
            .Where(j => j.CloudId == cloud.Id)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var recentEvents = job is null
            ? Array.Empty<EventSummary>()
            : ParseEventsTail(job.EventsLog, RecentEventsTake);

        return new CloudStatusResponse(
            CloudId:             cloud.Id,
            Hostname:            cloud.Hostname,
            Provider:            cloud.Provider,
            Region:              cloud.Region,
            ProvisioningStatus:  cloud.ProvisioningStatus,
            SucceededAt:         cloud.ProvisioningCompletedAt,
            DestroyedAt:         cloud.DestroyedAt,
            CancelRequestedAt:   cloud.CancelRequestedAt,
            CurrentJob:          job is null ? null : new JobSummary(job.Id, job.Kind, job.Status),
            RecentEvents:        recentEvents,
            PriceMonthlyUsd:     cloud.PriceMonthlyUsd,
            PriceHourlyUsd:      cloud.PriceHourlyUsd,
            PriceCurrency:       cloud.PriceCurrency,
            PricedAt:            cloud.PricedAt);
    }

    internal static IReadOnlyList<EventSummary> ParseEventsTail(JsonDocument log, int take)
    {
        if (log.RootElement.ValueKind != JsonValueKind.Array) return [];

        var array = log.RootElement;
        var total = array.GetArrayLength();
        var start = Math.Max(0, total - take);
        var list = new List<EventSummary>(Math.Min(total, take));
        for (var i = start; i < total; i++)
        {
            var el = array[i];
            var phase     = el.TryGetProperty("phase", out var p)     ? p.GetString() ?? "" : "";
            var ev        = el.TryGetProperty("event", out var e)     ? e.GetString() : null;
            var err       = el.TryGetProperty("error", out var er)    ? er.GetString() : null;
            var timestamp = el.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "";
            list.Add(new EventSummary(phase, ev, err, timestamp));
        }
        return list;
    }
}
