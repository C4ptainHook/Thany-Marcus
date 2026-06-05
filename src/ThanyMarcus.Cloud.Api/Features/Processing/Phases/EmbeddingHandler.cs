using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class EmbeddingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Embedding;

    private readonly CloudDbContext db;
    private readonly IEmbeddingClient embeddings;
    private readonly JobStateTransitions transitions;
    private readonly IIngestEventBus eventBus;
    private readonly ProvenanceMaterializer provenance;
    private readonly LlmEventAppender events;
    private readonly IOptions<GraniteEmbeddingOptions> embeddingOpts;

    public EmbeddingHandler(
        CloudDbContext db,
        IEmbeddingClient embeddings,
        JobStateTransitions transitions,
        IIngestEventBus eventBus,
        ProvenanceMaterializer provenance,
        LlmEventAppender events,
        IOptions<GraniteEmbeddingOptions> embeddingOpts)
    {
        this.db = db;
        this.embeddings = embeddings;
        this.transitions = transitions;
        this.eventBus = eventBus;
        this.provenance = provenance;
        this.events = events;
        this.embeddingOpts = embeddingOpts;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        var text = note.BodyOutput ?? note.BodyInput ?? string.Empty;
        var template = string.Equals(job.Kind, IngestJobKind.UserEditEmbed, StringComparison.Ordinal)
            ? "user-edit-v1"
            : job.LastComposeTemplate ?? "unknown";
        var newHash = ComputeBodyHash(template, text);

        var skip = string.Equals(job.Kind, IngestJobKind.Reprocess, StringComparison.Ordinal)
            && note.Embedding is not null
            && string.Equals(note.BodyHash, newHash, StringComparison.Ordinal);

        if (skip)
        {
            note.BodyHash = newHash;
            note.Status = NoteStatus.Ready;
            note.TransitionVersion += 1;
            await db.SaveChangesAsync(ct);

            await events.AppendEmbeddingSkipAsync(job.Id, newHash, ct);
        }
        else
        {
            var opts = embeddingOpts.Value;
            var sw = Stopwatch.StartNew();
            var vector = await embeddings.EmbedAsync(text, ct);
            sw.Stop();

            note.Embedding = new Vector(vector);
            note.BodyHash = newHash;
            note.Status = NoteStatus.Ready;
            note.TransitionVersion += 1;
            await db.SaveChangesAsync(ct);

            await events.AppendEmbeddingEmitAsync(
                job.Id,
                model: opts.ModelTag,
                modelVersion: opts.ModelRevision,
                durationMs: sw.ElapsedMilliseconds,
                dim: vector.Length,
                bodyHash: newHash,
                ct);
        }

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

    internal const string EmbedConfigTag = "granite-cls-256-v1";

    internal static string ComputeBodyHash(string template, string body)
    {
        var hashInput = EmbedConfigTag + "\n" + (template ?? "unknown") + "\n" + (body ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(bytes);
    }
}
