using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Folders;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public static class SyncMaintenanceEndpoints
{
    public const int MaxStatusNotes = 500;

    public static void MapSyncMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapPost("/notes/{noteId:guid}/revive", ReviveAsync)
            .WithName("SyncReviveNote")
            .Produces<SyncReviveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/status", StatusAsync)
            .WithName("SyncStatus")
            .Produces<SyncStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/desired", DesiredAsync)
            .WithName("SyncDesired")
            .Produces<SyncDesiredResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/entities/{entityId:guid}/regenerate-hub", RegenerateHubAsync)
            .WithName("SyncRegenerateHub")
            .Produces<RegenerateHubResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/notes/{noteId:guid}/move", MoveAsync)
            .WithName("SyncMoveNote")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/folders", ListFoldersAsync)
            .WithName("SyncListFolders")
            .Produces<FolderListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/folders", RegisterFolderAsync)
            .WithName("SyncRegisterFolder")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapDelete("/folders", UnregisterFolderAsync)
            .WithName("SyncUnregisterFolder")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/inbox/reroute", InboxRerouteAsync)
            .WithName("SyncInboxReroute")
            .Produces<FolderDissolveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> ListFoldersAsync(CloudDbContext db, CancellationToken ct)
    {
        var folders = await db.Folders
            .Where(f => f.DeletedAt == null)
            .OrderBy(f => f.Path)
            .Select(f => f.Path)
            .ToListAsync(ct);
        return Results.Ok(new FolderListResponse(folders));
    }

    private static async Task<IResult> RegisterFolderAsync(
        FolderRef req,
        CloudDbContext db,
        IClock clock,
        FolderRouter router,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        InboxRerouteSignal rerouteSignal,
        CancellationToken ct)
    {
        var path = (req.Folder ?? "").Trim().Trim('/');
        if (path.Length == 0) return Results.BadRequest();

        var hadCandidates = (await router.LoadCandidatesAsync(opts.CurrentValue.RoutingFoldersMax, ct)).Count > 0;

        var now = clock.GetCurrentInstant();
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO folders (id, path, created_at, updated_at)
            VALUES ({id}, {path}, {now}, {now})
            ON CONFLICT (path) DO UPDATE SET deleted_at = NULL, updated_at = {now}
            """, ct);

        if (!hadCandidates && FolderRouter.CandidateSegment(path) is not null)
        {
            rerouteSignal.Trigger();
        }
        return Results.NoContent();
    }

    private static async Task<IResult> InboxRerouteAsync(
        InboxRerouteService service,
        CancellationToken ct)
    {
        var result = await service.RunAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> UnregisterFolderAsync(
        [FromBody] FolderRef req,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var path = (req.Folder ?? "").Trim().Trim('/');
        if (path.Length == 0) return Results.BadRequest();

        var now = clock.GetCurrentInstant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE folders SET deleted_at = {now}, updated_at = {now}
              WHERE path = {path} AND deleted_at IS NULL
            """, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MoveAsync(
        Guid noteId,
        SyncMoveRequest req,
        CloudDbContext db,
        CancellationToken ct)
    {
        var path = (req.RelativePath ?? "").Trim().Trim('/');
        if (path.Length == 0) return Results.BadRequest();

        // Local-master: the vault decided where the note lives. Update relative_path so a later
        // reprocess writes to the new location; do NOT bump updated_at (no pull echo to this device).
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                relative_path      = {path},
                transition_version = transition_version + 1
              WHERE id = {noteId}
                AND deleted_at IS NULL
            """, ct);
        return rows == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> ReviveAsync(
        Guid noteId,
        NoteTombstoneService tombstones,
        CancellationToken ct)
    {
        var outcome = await tombstones.ReviveAsync(noteId, ct);
        return outcome switch
        {
            ReviveOutcome.Revived   => Results.Ok(new SyncReviveResponse(Revived: true)),
            ReviveOutcome.AlreadyLive => Results.Ok(new SyncReviveResponse(Revived: false)),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> StatusAsync(
        string? notes,
        CloudDbContext db,
        CancellationToken ct)
    {
        var ids = ParseGuidCsv(notes).Take(MaxStatusNotes).ToList();
        if (ids.Count == 0)
        {
            return Results.Ok(new SyncStatusResponse(Array.Empty<SyncStatusItem>()));
        }

        var rows = await db.Notes
            .Where(n => ids.Contains(n.Id))
            .Select(n => new { n.Id, n.Status, n.DeletedAt })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new SyncStatusItem(r.Id, r.DeletedAt is not null ? "deleted" : r.Status))
            .ToList();
        return Results.Ok(new SyncStatusResponse(items));
    }

    private static async Task<IResult> DesiredAsync(
        string? folder,
        CloudDbContext db,
        CancellationToken ct)
    {
        // Folders-are-projects: a folder is a relative_path prefix, never a project entity.
        var name = (folder ?? "Inbox").Trim().Trim('/');
        if (name.Length == 0) name = "Inbox";
        var prefix = name + "/";

        var rows = await db.Notes
            .Where(n => n.DeletedAt == null
                        && n.Status == NoteStatus.Ready
                        && n.RelativePath != null
                        && n.RelativePath.StartsWith(prefix))
            .Select(n => new { n.Id, n.RelativePath })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new SyncDesiredItem(r.Id, r.RelativePath ?? $"Inbox/{r.Id}.md"))
            .ToList();
        return Results.Ok(new SyncDesiredResponse(items));
    }

    private static async Task<IResult> RegenerateHubAsync(
        Guid entityId,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == entityId && e.DeletedAt == null, ct);
        if (entity is null) return Results.NotFound();

        var now = clock.GetCurrentInstant();
        entity.HubSuppressed = false;
        entity.UpdatedAt = now;

        Guid hubNoteId;
        if (entity.HubNoteId is { } existingHubId)
        {
            var hub = await db.Notes.FirstOrDefaultAsync(n => n.Id == existingHubId, ct);
            if (hub is not null && hub.DeletedAt is not null)
            {
                hub.DeletedAt = null;
                hub.TransitionVersion += 1;
                hub.UpdatedAt = now;
            }
            hubNoteId = existingHubId;
            HubMaterializer.EnqueueRegen(db, entity, clock);
        }
        else
        {
            var hub = HubMaterializer.MaterializeAsync(db, entity, clock);
            hubNoteId = hub.Id;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // ix_ingest_jobs_active_per_note collision — a regen is already in flight; coalesce.
        }

        await NotifyAsync(db, JobOrchestratorWorker.NewChannel, hubNoteId.ToString(), ct);
        return Results.Ok(new RegenerateHubResponse(hubNoteId));
    }

    private static IEnumerable<Guid> ParseGuidCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) yield break;
        var seen = new HashSet<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g) && seen.Add(g)) yield return g;
        }
    }

    internal static async Task NotifyAsync(CloudDbContext db, string channel, string payload, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd.Parameters.AddWithValue("chan", channel);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }
}
