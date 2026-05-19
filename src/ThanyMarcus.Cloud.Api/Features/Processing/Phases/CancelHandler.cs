using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class CancelHandler
{
    private readonly CloudDbContext db;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;
    private readonly IIngestEventBus eventBus;
    private readonly ProvenanceMaterializer provenance;

    public CancelHandler(
        CloudDbContext db,
        IClock clock,
        JobStateTransitions transitions,
        IIngestEventBus eventBus,
        ProvenanceMaterializer provenance)
    {
        this.db = db;
        this.clock = clock;
        this.transitions = transitions;
        this.eventBus = eventBus;
        this.provenance = provenance;
    }

    public async Task HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = clock.GetCurrentInstant();
        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        note.Status = NoteStatus.Failed;
        note.TransitionVersion += 1;
        note.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.DeadLettered,
            lastError: "user_cancelled",
            clearLease: true,
            setFinishedAt: true,
            ct);

        await provenance.MaterializeAndPersistAsync(job, ct);
        await eventBus.PublishNoteFailedAsync(job.NoteId, "user_cancelled", ct);
    }
}
