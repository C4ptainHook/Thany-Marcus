using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class ExtractingEntitiesHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.ExtractingEntities;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;

    public ExtractingEntitiesHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        IClock clock,
        JobStateTransitions transitions)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.clock = clock;
        this.transitions = transitions;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var attachments = await db.Attachments
            .Where(a => a.NoteId == job.NoteId)
            .ToListAsync(ct);

        var llm = llmFactory.Resolve(settings);
        var attachmentTexts = attachments
            .Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Extracted &&
                        !string.IsNullOrWhiteSpace(a.ExtractedText))
            .Select(a => new AttachmentText(a.Id, a.Kind, a.ExtractedText!))
            .ToList();

        var enriched = await llm.EnrichCompositeAsync(
            new CompositeEnrichmentRequest(note.BodyInput, attachmentTexts), ct);

        var now = clock.GetCurrentInstant();
        foreach (var anchor in enriched.BodyAnchors)
        {
            await CreateMentionAsync(note.Id, anchor, now, ct);
        }
        foreach (var group in enriched.AttachmentAnchors)
        {
            foreach (var anchor in group.Anchors)
            {
                await CreateMentionAsync(note.Id, anchor, now, ct);
            }
        }

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Embedding,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }

    private async Task CreateMentionAsync(Guid noteId, WikilinkAnchor anchor, Instant now, CancellationToken ct)
    {
        var canonical = anchor.Target;
        var entity = await db.Entities.SingleOrDefaultAsync(
            e => e.Kind == EntityKind.Concept && e.CanonicalName == canonical && e.DeletedAt == null,
            ct);

        if (entity is null)
        {
            entity = new Entity
            {
                Id            = Guid.CreateVersion7(),
                Kind          = EntityKind.Concept,
                CanonicalName = canonical,
                Source        = EntitySource.Llm,
                IsProvisional = true,
            };
            db.Entities.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        entity.MentionCount += 1;

        db.Mentions.Add(new Mention
        {
            Id          = Guid.CreateVersion7(),
            EntityId    = entity.Id,
            NoteId      = noteId,
            AnchorText  = anchor.Text,
            StartOffset = anchor.Start,
            EndOffset   = anchor.End,
            CreatedAt   = now,
        });
    }
}
