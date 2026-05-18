using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed partial class CompositeIngestHandler : IIngestJobHandler
{
    private readonly CloudDbContext db;
    private readonly IUrlExtractor urlExtractor;
    private readonly IImageExtractor imageExtractor;
    private readonly IVoiceExtractor voiceExtractor;
    private readonly IFileExtractor fileExtractor;
    private readonly ILlmClientFactory llmFactory;
    private readonly IClock clock;
    private readonly ILogger<CompositeIngestHandler> log;

    public CompositeIngestHandler(
        CloudDbContext db,
        IUrlExtractor urlExtractor,
        IImageExtractor imageExtractor,
        IVoiceExtractor voiceExtractor,
        IFileExtractor fileExtractor,
        ILlmClientFactory llmFactory,
        IClock clock,
        ILogger<CompositeIngestHandler> log)
    {
        this.db = db;
        this.urlExtractor = urlExtractor;
        this.imageExtractor = imageExtractor;
        this.voiceExtractor = voiceExtractor;
        this.fileExtractor = fileExtractor;
        this.llmFactory = llmFactory;
        this.clock = clock;
        this.log = log;
    }

    public async Task HandleAsync(IngestJob job, CancellationToken ct)
    {
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == job.NoteId, ct)
            ?? throw new InvalidOperationException($"note {job.NoteId} not found");
        var attachments = await db.Attachments.Where(a => a.NoteId == job.NoteId).ToListAsync(ct);
        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);

        foreach (var att in attachments.Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Pending))
        {
            try
            {
                await DispatchExtractionAsync(att, ct);
            }
            catch (Exception ex)
            {
                att.ExtractionStatus = AttachmentExtractionStatus.Failed;
                att.ExtractionError  = ex.Message;
                LogExtractionFailed(log, ex, att.Id, att.Kind);
            }
        }

        await db.SaveChangesAsync(ct);

        var llm = llmFactory.Resolve(settings);

        var attachmentTexts = attachments
            .Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Extracted && !string.IsNullOrWhiteSpace(a.ExtractedText))
            .Select(a => new AttachmentText(a.Id, a.Kind, a.ExtractedText!))
            .ToList();

        var enriched = await llm.EnrichCompositeAsync(
            new CompositeEnrichmentRequest(note.BodyInput, attachmentTexts), ct);

        var relativePath = enriched.SuggestedProject is { } project
            ? $"Projects/{CompositeMarkdownAssembler.SanitizeProject(project)}/{note.Id}.md"
            : $"Inbox/{note.Id}.md";

        var body = CompositeMarkdownAssembler.Assemble(note, attachments, enriched, llm.Mode);

        note.Status            = NoteStatus.Ready;
        note.RelativePath      = relativePath;
        note.BodyOutput        = body;
        note.SuggestedProject  = enriched.SuggestedProject;
        note.Tags              = enriched.Tags.Count > 0 ? enriched.Tags.ToArray() : null;
        note.LlmMode           = llm.Mode;
        note.Provenance        = BuildProvenance(llm, enriched, attachments);

        job.Status      = IngestJobStatus.Succeeded;
        job.FinishedAt  = clock.GetCurrentInstant();

        await db.SaveChangesAsync(ct);
    }

    private async Task DispatchExtractionAsync(Attachment att, CancellationToken ct)
    {
        if (att.Kind == AttachmentKind.Url)
        {
            if (!att.Extra.RootElement.TryGetProperty("url", out var u) ||
                u.ValueKind != JsonValueKind.String)
            {
                att.ExtractionStatus = AttachmentExtractionStatus.Failed;
                att.ExtractionError  = "missing extra.url";
                return;
            }
            var url = u.GetString()!;
            var result = await urlExtractor.ExtractAsync(url, ct);
            att.ExtractedText    = result.Markdown;
            att.ExtractionStatus = AttachmentExtractionStatus.Extracted;
        }
        else
        {
            try
            {
                string? text = att.Kind switch
                {
                    AttachmentKind.Image => await imageExtractor.ExtractAsync(att.StorageKey, ct),
                    AttachmentKind.Voice => await voiceExtractor.ExtractAsync(att.StorageKey, ct),
                    AttachmentKind.File  => await fileExtractor.ExtractAsync(att.StorageKey, att.MimeType, ct),
                    _ => null,
                };
                if (text is not null)
                {
                    att.ExtractedText    = text;
                    att.ExtractionStatus = AttachmentExtractionStatus.Extracted;
                }
                else
                {
                    att.ExtractionStatus = AttachmentExtractionStatus.Skipped;
                }
            }
            catch (NotImplementedException)
            {
                att.ExtractionStatus = AttachmentExtractionStatus.Skipped;
            }
        }
    }

    private JsonDocument BuildProvenance(
        ILlmClient llm,
        CompositeEnrichmentResult enriched,
        IReadOnlyList<Attachment> attachments)
    {
        var failedIds = attachments
            .Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Failed)
            .Select(a => a.Id.ToString())
            .ToList();

        var payload = new
        {
            model              = llm.ModelName,
            generated_at       = clock.GetCurrentInstant().ToString(),
            input_tokens       = enriched.InputTokens,
            output_tokens      = enriched.OutputTokens,
            anchor_count       = enriched.BodyAnchors.Count + enriched.AttachmentAnchors.Sum(a => a.Anchors.Count),
            extraction_failures = failedIds,
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Extraction failed for attachment {AttachmentId} ({Kind})")]
    private static partial void LogExtractionFailed(ILogger logger, Exception ex, Guid attachmentId, string kind);
}
