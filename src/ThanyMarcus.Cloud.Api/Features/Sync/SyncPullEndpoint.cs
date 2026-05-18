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
        CloudDbContext db,
        IArtifactStore store,
        CancellationToken ct)
    {
        var sinceInstant = since.HasValue
            ? Instant.FromDateTimeOffset(since.Value)
            : Instant.MinValue;
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, 200);

        var notes = await db.Notes
            .Where(n => n.Status == NoteStatus.Ready && n.UpdatedAt > sinceInstant)
            .OrderBy(n => n.UpdatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        if (notes.Count == 0)
        {
            return Results.Ok(new SyncPullResponse(
                Items: Array.Empty<SyncPullItem>(),
                NextSince: null));
        }

        var noteIds = notes.Select(n => n.Id).ToList();
        var attachments = await db.Attachments
            .Where(a => noteIds.Contains(a.NoteId))
            .ToListAsync(ct);

        var items = new List<SyncPullItem>(notes.Count);
        foreach (var note in notes)
        {
            var noteAtts = attachments.Where(a => a.NoteId == note.Id).ToList();
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

            items.Add(new SyncPullItem(
                NoteId:           note.Id,
                RelativePath:     note.RelativePath ?? $"Inbox/{note.Id}.md",
                Body:             note.BodyOutput ?? string.Empty,
                SuggestedProject: note.SuggestedProject,
                Tags:             note.Tags ?? Array.Empty<string>(),
                LlmMode:          note.LlmMode,
                Attachments:      dtoAtts,
                UpdatedAt:        note.UpdatedAt.ToDateTimeOffset()));
        }

        var next = notes[^1].UpdatedAt.ToDateTimeOffset();
        return Results.Ok(new SyncPullResponse(items, next));
    }
}
