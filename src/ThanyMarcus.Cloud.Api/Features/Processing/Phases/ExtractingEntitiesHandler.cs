using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed partial class ExtractingEntitiesHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.ExtractingEntities;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly IEmbeddingClient embeddings;
    private readonly EntitySuggestionAggregator suggestions;
    private readonly LlmEventAppender events;
    private readonly IIngestEventBus eventBus;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;
    private readonly ILogger<ExtractingEntitiesHandler> log;

    public ExtractingEntitiesHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        IEmbeddingClient embeddings,
        EntitySuggestionAggregator suggestions,
        LlmEventAppender events,
        IIngestEventBus eventBus,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock,
        JobStateTransitions transitions,
        ILogger<ExtractingEntitiesHandler> log)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.embeddings = embeddings;
        this.suggestions = suggestions;
        this.events = events;
        this.eventBus = eventBus;
        this.opts = opts;
        this.clock = clock;
        this.transitions = transitions;
        this.log = log;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        if (note.DeletedAt is not null)
        {
            return PhaseHandlerResult.Advanced;
        }

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var llm = llmFactory.Resolve(settings, out var fellBackToSafe);
        var o = opts.CurrentValue;

        var attachments = await db.Attachments
            .Where(a => a.NoteId == note.Id)
            .ToListAsync(ct);
        var body = RawExtractions.Concatenate(note, attachments);
        if (string.IsNullOrEmpty(body)) body = note.BodyInput ?? string.Empty;

        var extractPid = new PromptId("extract", "v1");
        var extractStart = clock.GetCurrentInstant();
        EntityExtractionDto extraction;
        try
        {
            var prompt = PromptBuilder.BuildExtract(body);
            extraction = await llm.CompleteAsync<EntityExtractionDto>(
                extractPid, new LlmPromptRequest(prompt), ct);
        }
        catch (LlmStructuredOutputException ex)
        {
            await events.AppendAsync(job.Id, BuildEvent(LlmEventStages.Extract, extractPid, llm, fellBackToSafe,
                ElapsedMs(extractStart), ex.Attempts - 1, "failed", null, null, ex.Message), ct);
            throw;
        }
        var mentions = extraction.Mentions ?? new List<MentionCandidateDto>();
        await events.AppendAsync(job.Id, BuildEvent(LlmEventStages.Extract, extractPid, llm, fellBackToSafe,
            ElapsedMs(extractStart), 0,
            decision: $"mentions:{mentions.Count}", confidence: null, rationale: null, error: null), ct);

        var thresholds = o.Thresholds;
        var candidates = new List<MentionCandidateDto>();
        foreach (var m in mentions)
        {
            if (m is null) continue;
            if (m.Confidence >= thresholds.MentionMin) candidates.Add(m);
        }

        var newMentions = new List<Mention>();
        var hubSpawnEntityIds = new HashSet<Guid>();
        var existingHubEntityIds = new HashSet<Guid>();
        var now = clock.GetCurrentInstant();

        foreach (var cand in candidates)
        {
            if (note.DeletedAt is not null) break;

            var candVec = await EntityEmbeddingHelper.EmbedCanonicalAsync(embeddings, cand.CandidateCanonical, ct);
            var candEmb = candVec.ToArray();

            // kNN against user-curated entities. A confident match (cosine distance within the gate)
            // persists a mention against that entity; this deterministic gate replaces the dedup LLM.
            var neighbors = await EntityVectorQueries.NearestWithDistanceAsync(
                db, cand.CandidateKind, candEmb, o.Pgvector.DedupTopK, ct);
            var best = neighbors.Count > 0 ? neighbors[0] : (EntityDistance?)null;

            Entity? target = null;
            if (best is { } match && match.Distance <= thresholds.SuggestionMatchDistance)
            {
                target = await db.Entities.SingleOrDefaultAsync(
                    e => e.Id == match.Id && e.DeletedAt == null, ct);
            }

            if (target is null)
            {
                // Not a known entity → accumulate as a suggestion for the user to curate.
                var surrounding = ExtractSurrounding(body, cand.StartOffset, cand.EndOffset, o.SurroundingTextChars);
                await suggestions.AppendOrCreateAsync(cand, note.Id, surrounding, candVec, now, ct);
                continue;
            }

            var alias = cand.AnchorText?.Trim() ?? "";
            if (alias.Length > 0 &&
                !target.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(target.CanonicalName, alias, StringComparison.OrdinalIgnoreCase))
            {
                target.Aliases = target.Aliases.Append(alias).ToArray();
            }

            target.MentionCount += 1;
            if (target.HubSuppressed)
            {
                // User deleted this entity's hub note; honour that until an explicit force-regen.
            }
            else if (target.HubNoteId is null && target.MentionCount >= o.HubMaterializeMin)
            {
                hubSpawnEntityIds.Add(target.Id);
            }
            else if (target.HubNoteId is not null)
            {
                existingHubEntityIds.Add(target.Id);
            }

            newMentions.Add(new Mention
            {
                Id = Guid.CreateVersion7(),
                EntityId = target.Id,
                NoteId = note.Id,
                AnchorText = cand.AnchorText ?? "",
                StartOffset = cand.StartOffset,
                EndOffset = cand.EndOffset,
                Confidence = (float)cand.Confidence,
                CreatedAt = now,
            });
        }

        db.Mentions.AddRange(newMentions);

        // Hub spawns — for entities whose mention_count just crossed the threshold
        var spawnedHubs = new List<(Guid entityId, Guid noteId)>();
        foreach (var entityId in hubSpawnEntityIds)
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            var hubNote = HubMaterializer.MaterializeAsync(db, entity, clock);
            spawnedHubs.Add((entityId, hubNote.Id));
        }

        // Diff-aware regen — entities that already had a hub got a fresh mention
        foreach (var entityId in existingHubEntityIds)
        {
            if (hubSpawnEntityIds.Contains(entityId)) continue;
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            HubMaterializer.EnqueueRegen(db, entity, clock);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // ix_ingest_jobs_active_per_note collision — the existing in-flight regen will see
            // the new mentions when it next runs extracting_entities. Coalesce silently.
            LogRegenCoalesced(log, ex);
        }

        foreach (var (entityId, hubNoteId) in spawnedHubs)
        {
            await eventBus.PublishHubMaterializedAsync(hubNoteId, entityId, ct);
        }

        // Hub-regen jobs jump directly to embedding once entities/mentions are settled —
        // they have no routing/synthesizing applicability.
        var nextStatus = note.IsHub ? IngestJobStatus.Embedding : IngestJobStatus.Routing;
        await transitions.TransitionAsync(
            job,
            nextStatus: nextStatus,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }

    private long ElapsedMs(Instant start) =>
        (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;

    private static LlmEvent BuildEvent(
        string stage, PromptId pid, ILlmClient llm, bool fallback, long durationMs, int retryIndex,
        string? decision, double? confidence, string? rationale, string? error) =>
        new(stage, pid.ToString(), llm.ModelName, llm.ModelVersion, llm.Mode, fallback,
            durationMs, retryIndex, decision, confidence, rationale, error);

    internal static string ExtractSurrounding(string body, int start, int end, int chars)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var lo = Math.Max(0, start - chars);
        var hi = Math.Min(body.Length, end + chars);
        if (hi <= lo) return "";
        return body[lo..hi];
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Hub regen enqueue coalesced (in-flight job already exists)")]
    private static partial void LogRegenCoalesced(ILogger logger, Exception ex);
}
