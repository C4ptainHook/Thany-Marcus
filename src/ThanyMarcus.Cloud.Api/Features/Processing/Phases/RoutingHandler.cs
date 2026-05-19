using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class RoutingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Routing;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly JobStateTransitions transitions;

    public RoutingHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        JobStateTransitions transitions)
    {
        this.db = db;
        this.llmFactory = llmFactory;
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

        note.LlmMode = llm.Mode;
        note.Tags    = enriched.Tags.Count > 0 ? enriched.Tags.ToArray() : null;

        if (!string.IsNullOrWhiteSpace(enriched.SuggestedProject))
        {
            var canonical = CompositeMarkdownAssembler.SanitizeProject(enriched.SuggestedProject);
            var project = await db.Entities.SingleOrDefaultAsync(
                e => e.Kind == EntityKind.Project && e.CanonicalName == canonical && e.DeletedAt == null,
                ct);

            if (project is null)
            {
                project = new Entity
                {
                    Id            = Guid.CreateVersion7(),
                    Kind          = EntityKind.Project,
                    CanonicalName = canonical,
                    Source        = EntitySource.Llm,
                    IsProvisional = true,
                };
                db.Entities.Add(project);
            }
            note.ProjectId    = project.Id;
            note.RelativePath = $"Projects/{canonical}/{note.Id}.md";
            note.SuggestedProject = enriched.SuggestedProject;
        }

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.ExtractingEntities,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }
}
