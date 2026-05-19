using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class ComposingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Composing;

    private readonly CloudDbContext db;
    private readonly CompositeNoteComposer composer;
    private readonly JobStateTransitions transitions;
    private readonly IClock clock;

    public ComposingHandler(
        CloudDbContext db,
        CompositeNoteComposer composer,
        JobStateTransitions transitions,
        IClock clock)
    {
        this.db = db;
        this.composer = composer;
        this.transitions = transitions;
        this.clock = clock;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var attachments = await db.Attachments
            .Where(a => a.NoteId == job.NoteId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);

        var composed = await composer.ComposeAsync(note, attachments, note.BodyOutput, ct);

        note.BodyOutput = $"---\n{composed.Frontmatter}---\n\n{composed.Body}";
        note.RelativePath ??= $"Inbox/{note.Id}.md";
        note.UpdatedAt = clock.GetCurrentInstant();
        job.LastComposeTemplate = composed.ComposeTemplateVersion;

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
