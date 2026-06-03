using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class IngestEndpoints
{
    public const int MaxBodyBytes        = 100 * 1024;
    public const int MaxAttachments      = 50;
    public const long MaxAttachmentBytes = 5L * 1024 * 1024 * 1024;
    public const long MaxTotalBytes      = 20L * 1024 * 1024 * 1024;
    public static readonly TimeSpan UploadUrlTtl = TimeSpan.FromMinutes(15);

    public static void MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ingest").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapPost("/init", IngestInitAsync)
             .WithName("PostIngestInit")
             .Produces<IngestInitResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{noteId:guid}/finalize", IngestFinalizeAsync)
             .WithName("PostIngestFinalize")
             .Produces<IngestFinalizeResponse>(StatusCodes.Status202Accepted)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> IngestInitAsync(
        IngestInitRequest req,
        CloudDbContext db,
        IArtifactStore store,
        StorageOptions storage,
        ISynthesisApiKeyStore apiKeyStore,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClientNoteId))
            return Results.Problem("clientNoteId is required", statusCode: StatusCodes.Status400BadRequest);
        if (req.Body is null)
            return Results.Problem("body is required", statusCode: StatusCodes.Status400BadRequest);
        if (System.Text.Encoding.UTF8.GetByteCount(req.Body) > MaxBodyBytes)
            return Results.Problem($"body exceeds {MaxBodyBytes} bytes", statusCode: StatusCodes.Status400BadRequest);
        if (req.Attachments.Count > MaxAttachments)
            return Results.Problem($"too many attachments (max {MaxAttachments})", statusCode: StatusCodes.Status400BadRequest);

        var privacyMode = req.PrivacyMode ?? PrivacyModes.Private;
        if (!PrivacyModes.IsValid(privacyMode))
            return Results.Problem($"unknown privacyMode '{req.PrivacyMode}'", statusCode: StatusCodes.Status400BadRequest);
        if (privacyMode == PrivacyModes.Public)
        {
            if (!PublicSynthesisModels.IsValid(req.PublicModel))
                return Results.Problem("publicModel required for public mode", statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(req.LlmApiKey))
                return Results.Problem("llmApiKey required for public mode", statusCode: StatusCodes.Status400BadRequest);
        }

        var preset = req.SynthesisPreset ?? Shared.PluginApi.SynthesisPresets.Zettelkasten;
        if (!Shared.PluginApi.SynthesisPresets.IsValid(preset))
            return Results.Problem($"unknown synthesisPreset '{req.SynthesisPreset}'", statusCode: StatusCodes.Status400BadRequest);
        if (preset == Shared.PluginApi.SynthesisPresets.Custom && string.IsNullOrWhiteSpace(req.CustomPrompt))
            return Results.Problem("customPrompt required when synthesisPreset = 'custom'", statusCode: StatusCodes.Status400BadRequest);

        long totalBinaryBytes = 0;
        foreach (var a in req.Attachments)
        {
            if (!AttachmentKind.IsValid(a.Kind))
                return Results.Problem($"unknown kind '{a.Kind}'", statusCode: StatusCodes.Status400BadRequest);

            var mode = a.Mode ?? AttachmentMode.Extract;
            if (!AttachmentMode.IsValid(mode))
                return Results.Problem($"unknown mode '{a.Mode}'", statusCode: StatusCodes.Status400BadRequest);
            if (!AttachmentMode.IsValidFor(mode, a.Kind))
                return Results.UnprocessableEntity(new { error = $"mode '{mode}' not valid for kind '{a.Kind}'" });

            if (AttachmentKind.IsBinary(a.Kind))
            {
                if (a.ByteSize is null or <= 0)
                    return Results.Problem($"byteSize required for kind '{a.Kind}'", statusCode: StatusCodes.Status400BadRequest);
                if (a.ByteSize > MaxAttachmentBytes)
                    return Results.Problem($"attachment exceeds {MaxAttachmentBytes} bytes", statusCode: StatusCodes.Status400BadRequest);
                if (string.IsNullOrWhiteSpace(a.Sha256))
                    return Results.Problem("sha256 required for binary attachment", statusCode: StatusCodes.Status400BadRequest);
                if (string.IsNullOrWhiteSpace(a.MimeType))
                    return Results.Problem("mimeType required for binary attachment", statusCode: StatusCodes.Status400BadRequest);

                totalBinaryBytes += a.ByteSize.Value;
            }
            else if (a.Kind == AttachmentKind.Url)
            {
                if (string.IsNullOrWhiteSpace(ExtractUrl(a.Extra)))
                    return Results.Problem("extra.url required for url attachment", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (totalBinaryBytes > MaxTotalBytes)
            return Results.Problem($"total attachment bytes exceed {MaxTotalBytes}", statusCode: StatusCodes.Status400BadRequest);

        var now = clock.GetCurrentInstant();
        var captured = Instant.FromDateTimeOffset(req.CapturedAt);

        var note = await db.Notes.SingleOrDefaultAsync(n => n.ClientNoteId == req.ClientNoteId, ct);
        bool created = false;
        if (note is null)
        {
            note = new Note
            {
                ClientNoteId       = req.ClientNoteId,
                CapturedAt         = captured,
                BodyInput          = req.Body,
                Status             = NoteStatus.Pending,
                PrivacyMode        = privacyMode,
                PublicModel        = privacyMode == PrivacyModes.Public ? req.PublicModel : null,
                SynthesisPreset    = preset,
                SynthesisPromptBody = preset == Shared.PluginApi.SynthesisPresets.Custom ? req.CustomPrompt : null,
                CreatedAt          = now,
                UpdatedAt          = now,
            };
            db.Notes.Add(note);
            created = true;
        }
        else if (note.Status != NoteStatus.Pending)
        {
            return Results.Problem(
                "note already finalized; cannot re-init",
                statusCode: StatusCodes.Status409Conflict);
        }

        var uploads = new List<IngestInitUpload>(req.Attachments.Count);
        var existingAttachments = created
            ? new List<Attachment>()
            : await db.Attachments.Where(a => a.NoteId == note.Id).ToListAsync(ct);

        foreach (var a in req.Attachments)
        {
            var existing = existingAttachments
                .FirstOrDefault(x => x.ClientAttachmentId == a.ClientAttachmentId);

            Attachment attachment;
            if (existing is null)
            {
                var newId = Guid.CreateVersion7();
                attachment = new Attachment
                {
                    Id                 = newId,
                    NoteId             = note.Id,
                    ClientAttachmentId = a.ClientAttachmentId,
                    Kind               = a.Kind,
                    Mode               = a.Mode ?? AttachmentMode.Extract,
                    StorageProvider    = storage.Provider,
                    StorageBucket      = storage.Bucket,
                    StorageKey         = BuildStorageKey(note.Id, newId, a),
                    ByteSize           = a.ByteSize,
                    MimeType           = a.MimeType,
                    Sha256             = a.Sha256,
                    Filename           = a.Filename,
                    Status             = AttachmentKind.IsBinary(a.Kind)
                                         ? AttachmentStatus.AwaitingUpload
                                         : AttachmentStatus.Uploaded,
                    Extra              = ToJsonDocument(a.Extra),
                    Url                = a.Kind == AttachmentKind.Url ? ExtractUrl(a.Extra) : null,
                    CreatedAt          = now,
                    UpdatedAt          = now,
                };
                db.Attachments.Add(attachment);
            }
            else
            {
                attachment = existing;
            }

            if (AttachmentKind.IsBinary(a.Kind))
            {
                var presigned = await store.IssueUploadUrlAsync(
                    attachment.StorageKey,
                    a.MimeType!,
                    a.ByteSize!.Value,
                    UploadUrlTtl,
                    ct);

                uploads.Add(new IngestInitUpload(
                    ClientAttachmentId: a.ClientAttachmentId,
                    AttachmentId:       attachment.Id,
                    UploadUrl:          presigned.Url.ToString(),
                    RequiredHeaders:    presigned.RequiredHeaders,
                    ExpiresAt:          presigned.ExpiresAt.ToDateTimeOffset()));
            }
        }

        await db.SaveChangesAsync(ct);

        if (privacyMode == PrivacyModes.Public && !string.IsNullOrEmpty(req.LlmApiKey))
        {
            apiKeyStore.Put(note.Id, req.LlmApiKey);
        }

        return Results.Ok(new IngestInitResponse(note.Id, uploads));
    }

    private static async Task<IResult> IngestFinalizeAsync(
        Guid noteId,
        IngestFinalizeRequest req,
        CloudDbContext db,
        IArtifactStore store,
        IClock clock,
        CancellationToken ct)
    {
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        if (note.Status == NoteStatus.Processing || note.Status == NoteStatus.Ready)
        {
            return Results.Accepted(value: new IngestFinalizeResponse(note.Id, note.Status));
        }
        if (note.Status != NoteStatus.Pending)
        {
            return Results.Problem(
                $"note in terminal status {note.Status}",
                statusCode: StatusCodes.Status409Conflict);
        }

        var attachments = await db.Attachments.Where(a => a.NoteId == noteId).ToListAsync(ct);
        var byId = attachments.ToDictionary(a => a.Id);

        var mismatches = new List<IngestFinalizeMismatchEntry>();

        foreach (var u in req.Uploaded)
        {
            if (!byId.TryGetValue(u.AttachmentId, out var att))
            {
                mismatches.Add(new IngestFinalizeMismatchEntry(u.AttachmentId, "unknown_attachment", null, null));
                continue;
            }
            if (!AttachmentKind.IsBinary(att.Kind))
            {
                continue;
            }

            var head = await store.HeadAsync(att.StorageKey, ct);
            if (head is null)
            {
                mismatches.Add(new IngestFinalizeMismatchEntry(att.Id, "missing_in_bucket", null, null));
                continue;
            }
            if (head.ByteSize != u.ByteSize)
            {
                mismatches.Add(new IngestFinalizeMismatchEntry(
                    att.Id, "byte_size_mismatch",
                    Expected: u.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Actual:   head.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            att.Status      = AttachmentStatus.Uploaded;
            att.ByteSize    = head.ByteSize;
            att.Sha256      = u.Sha256;
        }

        var stillAwaiting = attachments.Any(a =>
            AttachmentKind.IsBinary(a.Kind) && a.Status != AttachmentStatus.Uploaded);
        if (stillAwaiting)
        {
            mismatches.Add(new IngestFinalizeMismatchEntry(
                Guid.Empty, "incomplete_uploads", null, null));
        }

        if (mismatches.Count > 0)
        {
            return Results.UnprocessableEntity(new { mismatches });
        }

        var now = clock.GetCurrentInstant();
        note.Status = NoteStatus.Processing;

        db.IngestJobs.Add(new IngestJob
        {
            Id           = Guid.CreateVersion7(),
            NoteId       = note.Id,
            Status       = IngestJobStatus.Queued,
            ScheduledAt  = now,
            CreatedAt    = now,
            UpdatedAt    = now,
        });

        await db.SaveChangesAsync(ct);

        await NotifyIngestJobAsync(db, ct);

        return Results.Accepted(value: new IngestFinalizeResponse(note.Id, NoteStatus.Processing));
    }

    private static async Task NotifyIngestJobAsync(CloudDbContext db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }
        await using var cmd = new NpgsqlCommand("NOTIFY ingest_jobs_new", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildStorageKey(Guid noteId, Guid attachmentId, IngestInitAttachment a)
    {
        var ext = AttachmentKind.IsBinary(a.Kind) ? MimeToExtension.Resolve(a.MimeType) : "url";
        return $"notes/{noteId}/{attachmentId}.{ext}";
    }

    private static JsonDocument ToJsonDocument(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(element.GetRawText());

    private static string? ExtractUrl(JsonElement extra) =>
        extra.ValueKind == JsonValueKind.Object
        && extra.TryGetProperty("url", out var u)
        && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
}
