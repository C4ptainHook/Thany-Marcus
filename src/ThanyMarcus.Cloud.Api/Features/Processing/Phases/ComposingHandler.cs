using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class ComposingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Composing;

    private readonly CloudDbContext db;
    private readonly JobStateTransitions transitions;

    public ComposingHandler(CloudDbContext db, JobStateTransitions transitions)
    {
        this.db = db;
        this.transitions = transitions;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        var attachments = await db.Attachments
            .Where(a => a.NoteId == job.NoteId)
            .ToListAsync(ct);

        var emptyEnrichment = new CompositeEnrichmentResult(
            SuggestedProject:  null,
            BodyAnchors:       Array.Empty<WikilinkAnchor>(),
            AttachmentAnchors: Array.Empty<AttachmentAnchors>(),
            Tags:              Array.Empty<string>(),
            InputTokens:       null,
            OutputTokens:      null);

        var body = CompositeMarkdownAssembler.Assemble(note, attachments, emptyEnrichment, llmMode: "safe");
        note.BodyOutput   = body;
        note.RelativePath = $"Inbox/{note.Id}.md";

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Routing,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }
}
