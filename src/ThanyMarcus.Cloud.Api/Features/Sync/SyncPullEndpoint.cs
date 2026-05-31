using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public static class SyncPullEndpoint
{
    public const int DefaultPageSize = 50;
    public static readonly TimeSpan DownloadUrlTtl = TimeSpan.FromHours(1);

    public static void MapSyncPullEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/sync/pull", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("GetSyncPull")
            .Produces<SyncPullResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> HandleAsync(
        DateTimeOffset? since,
        int? limit,
        string? include,
        CloudDbContext db,
        IArtifactStore store,
        CancellationToken ct)
    {
        var sinceInstant = since.HasValue
            ? Instant.FromDateTimeOffset(since.Value)
            : Instant.MinValue;
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, 200);

        var includeProvenance = !string.IsNullOrEmpty(include) &&
            include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Any(t => string.Equals(t, "provenance", StringComparison.OrdinalIgnoreCase));

        var notes = await db.Notes
            .Where(n => n.UpdatedAt > sinceInstant &&
                        (n.Status == NoteStatus.Ready || n.DeletedAt != null))
            .OrderBy(n => n.UpdatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = new List<SyncPullItem>(notes.Count);
        if (notes.Count > 0)
        {
            var liveNoteIds = notes.Where(n => n.DeletedAt is null).Select(n => n.Id).ToList();
            var attachments = liveNoteIds.Count == 0
                ? new List<Attachment>()
                : await db.Attachments
                    .Where(a => liveNoteIds.Contains(a.NoteId))
                    .ToListAsync(ct);

            foreach (var note in notes)
            {
                if (note.DeletedAt is not null)
                {
                    items.Add(new SyncPullItem(
                        NoteId:           note.Id,
                        RelativePath:     note.RelativePath ?? $"Inbox/{note.Id}.md",
                        Body:             string.Empty,
                        SuggestedProject: null,
                        Tags:             Array.Empty<string>(),
                        LlmMode:          null,
                        Attachments:      Array.Empty<SyncPullAttachment>(),
                        UpdatedAt:        note.UpdatedAt.ToDateTimeOffset(),
                        Deleted:          true,
                        Provenance:       null,
                        Status:           includeProvenance ? note.Status : null,
                        DeletedAt:        note.DeletedAt?.ToDateTimeOffset()));
                    continue;
                }

                var noteAtts = attachments.Where(a => a.NoteId == note.Id).ToList();
                var failures = noteAtts
                    .Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Failed)
                    .Select(a => new SyncPullExtractionFailure(
                        Kind: a.Kind,
                        AttachmentId: a.Id,
                        Reason: a.ExtractionError ?? "unknown",
                        Soft: true))
                    .ToList();
                var dtoAtts = new List<SyncPullAttachment>(noteAtts.Count);
                foreach (var att in noteAtts)
                {
                    string? downloadUrl = null;
                    DateTimeOffset? expiresAt = null;
                    if (AttachmentKind.IsBinary(att.Kind))
                    {
                        var presigned = await store.IssueDownloadUrlAsync(att.StorageKey, DownloadUrlTtl, ct);
                        downloadUrl = presigned.Url.ToString();
                        expiresAt   = presigned.ExpiresAt.ToDateTimeOffset();
                    }
                    dtoAtts.Add(new SyncPullAttachment(
                        AttachmentId:         att.Id,
                        Kind:                 att.Kind,
                        Filename:             att.Filename,
                        MimeType:             att.MimeType,
                        ByteSize:             att.ByteSize,
                        Sha256:               att.Sha256,
                        DownloadUrl:          downloadUrl,
                        DownloadUrlExpiresAt: expiresAt,
                        Extra:                att.Extra.RootElement.Clone()));
                }

                System.Text.Json.JsonElement? provenance = null;
                string? statusField = null;
                if (includeProvenance)
                {
                    provenance  = note.Provenance is null ? null : note.Provenance.RootElement.Clone();
                    statusField = note.Status;
                }

                items.Add(new SyncPullItem(
                    NoteId:             note.Id,
                    RelativePath:       note.RelativePath ?? $"Inbox/{note.Id}.md",
                    Body:               note.BodyOutput ?? string.Empty,
                    SuggestedProject:   note.SuggestedProject,
                    Tags:               note.Tags ?? Array.Empty<string>(),
                    LlmMode:            note.LlmMode,
                    Attachments:        dtoAtts,
                    UpdatedAt:          note.UpdatedAt.ToDateTimeOffset(),
                    Deleted:            false,
                    Provenance:         provenance,
                    Status:             statusField,
                    DeletedAt:          null,
                    ExtractionFailures: failures.Count == 0 ? null : failures));
            }
        }

        DateTimeOffset? nextSince = items.Count > 0 ? items.Max(i => i.UpdatedAt) : null;

        return Results.Ok(new SyncPullResponse(items, nextSince));
    }
}
