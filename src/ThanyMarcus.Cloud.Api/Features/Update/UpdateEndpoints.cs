using Microsoft.AspNetCore.Mvc;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Shared.CloudUpdate;
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Cloud.Api.Features.Update;

public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/update").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapGet("/status", (CloudUpdateStateStore store) => Results.Ok(store.ReadStatus()))
            .WithName("CloudUpdateStatus")
            .Produces<CloudUpdateStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/apply", ApplyAsync)
            .WithName("CloudUpdateApply")
            .Produces<ApplyUpdateResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ApplyAsync(
        [FromBody] ApplyUpdateRequest bundle,
        CloudUpdateStateStore store,
        CloudVersionReader version,
        CancellationToken ct)
    {
        if (!SemVer.TryParse(bundle.Version, out var target))
            return Results.BadRequest(Reject(bundle.Version, "invalid_version"));
        if (!ReleaseStrategies.IsValid(bundle.Strategy))
            return Results.BadRequest(Reject(bundle.Version, "invalid_strategy"));
        if (string.IsNullOrWhiteSpace(bundle.ComposeYaml))
            return Results.BadRequest(Reject(bundle.Version, "missing_compose"));

        if (bundle.Strategy == ReleaseStrategies.BlueGreen)
            return Results.Conflict(new ApplyUpdateResponse(false, CloudUpdatePhases.RequiresBlueGreen, bundle.Version, "blue_green_required"));

        var current = version.CurrentVersion();
        if (SemVer.TryParse(current, out var currentVer))
        {
            if (target <= currentVer)
                return Results.Conflict(new ApplyUpdateResponse(false, CloudUpdatePhases.UpToDate, bundle.Version, "not_newer_than_current"));

            if (SemVer.TryParse(bundle.SchemaMinFrom, out var minFrom) && currentVer < minFrom)
                return Results.Conflict(new ApplyUpdateResponse(false, CloudUpdatePhases.RequiresBlueGreen, bundle.Version, "schema_min_from_not_met"));
        }

        if (store.IsBusy())
            return Results.Conflict(new ApplyUpdateResponse(false, store.ReadStatus().Phase, bundle.Version, "update_in_progress"));

        await store.QueueAsync(bundle, ct);
        return Results.Accepted("/api/update/status", new ApplyUpdateResponse(true, CloudUpdatePhases.Queued, bundle.Version, "Update queued"));
    }

    private static ApplyUpdateResponse Reject(string? version, string message) =>
        new(false, CloudUpdatePhases.Failed, version ?? "", message);
}
