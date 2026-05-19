using Microsoft.EntityFrameworkCore;
using Pgvector;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class EmbeddingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Embedding;

    private readonly CloudDbContext db;
    private readonly IEmbeddingClient embeddings;
    private readonly JobStateTransitions transitions;
    private readonly IIngestEventBus eventBus;
    private readonly ProvenanceMaterializer provenance;

    public EmbeddingHandler(
        CloudDbContext db,
        IEmbeddingClient embeddings,
        JobStateTransitions transitions,
        IIngestEventBus eventBus,
        ProvenanceMaterializer provenance)
    {
        this.db = db;
        this.embeddings = embeddings;
        this.transitions = transitions;
        this.eventBus = eventBus;
        this.provenance = provenance;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        var text = note.BodyOutput ?? note.BodyInput;
        var vector = await embeddings.EmbedAsync(text, ct);
        note.Embedding = new Vector(vector);
        note.Status = NoteStatus.Ready;
        note.TransitionVersion += 1;

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Succeeded,
            lastError: null,
            clearLease: true,
            setFinishedAt: true,
            ct);

        await provenance.MaterializeAndPersistAsync(job, ct);
        await eventBus.PublishNoteSucceededAsync(job.NoteId, ct);
        return PhaseHandlerResult.Advanced;
    }
}
