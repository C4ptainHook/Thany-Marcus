using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public static class FolderDissolveEndpoint
{
    public static void MapFolderDissolveEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/sync/folders/dissolve", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("SyncFolderDissolve")
            .Produces<FolderDissolveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> HandleAsync(
        FolderDissolveRequest req,
        NoteTombstoneService tombstones,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var folder = (req.Folder ?? "").Trim().Trim('/');
        if (folder.Length == 0 || !FolderDissolveMode.IsValid(req.Mode))
        {
            return Results.BadRequest();
        }

        var prefix = folder + "/";

        if (req.Mode == FolderDissolveMode.ForceDelete)
        {
            var ids = await db.Notes
                .Where(n => n.DeletedAt == null
                            && n.RelativePath != null
                            && n.RelativePath.StartsWith(prefix))
                .Select(n => n.Id)
                .ToListAsync(ct);

            var count = 0;
            foreach (var id in ids)
            {
                if (await tombstones.TombstoneAsync(id, ct) == TombstoneOutcome.Tombstoned)
                {
                    count++;
                }
            }
            if (count > 0)
            {
                await SyncMaintenanceEndpoints.NotifyAsync(db, JobOrchestratorWorker.ChangedChannel, folder, ct);
            }
            return Results.Ok(new FolderDissolveResponse(req.Mode, count, Array.Empty<SyncDesiredItem>()));
        }

        // reroute: move every note under the folder to the target (Inbox by default), keeping the
        // basename. relative_path is the routing source of truth (folders-are-projects), so this is
        // the whole reassign — atomic, idempotent (re-running on an already-empty folder is a no-op).
        var target = (req.TargetFolder ?? "Inbox").Trim().Trim('/');
        if (target.Length == 0) target = "Inbox";
        var targetPrefix = target + "/";
        var like = EscapeLike(folder) + "/%";
        var now = clock.GetCurrentInstant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                relative_path      = {targetPrefix} || regexp_replace(relative_path, '^.*/', ''),
                updated_at         = {now},
                transition_version = transition_version + 1
              WHERE deleted_at IS NULL
                AND relative_path LIKE {like} ESCAPE '\'
            """, ct);
        await tx.CommitAsync(ct);

        var desired = await db.Notes
            .Where(n => n.DeletedAt == null
                        && n.Status == NoteStatus.Ready
                        && n.RelativePath != null
                        && n.RelativePath.StartsWith(targetPrefix))
            .Select(n => new SyncDesiredItem(n.Id, n.RelativePath!))
            .ToListAsync(ct);

        return Results.Ok(new FolderDissolveResponse(req.Mode, affected, desired));
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
