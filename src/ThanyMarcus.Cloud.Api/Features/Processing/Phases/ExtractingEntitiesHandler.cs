using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Pgvector;
using ThanyMarcus.Cloud.Api.Features.Entities;
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

        var body = note.BodyOutput ?? note.BodyInput ?? string.Empty;

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
        await events.AppendAsync(job.Id, BuildEvent(LlmEventStages.Extract, extractPid, llm, fellBackToSafe,
            ElapsedMs(extractStart), 0,
            decision: $"mentions:{extraction.Mentions.Count}", confidence: null, rationale: null, error: null), ct);

        var thresholds = o.Thresholds;
        var candidates = new List<MentionCandidateDto>();
        foreach (var m in extraction.Mentions)
        {
            if (m.Confidence >= thresholds.MentionMin) candidates.Add(m);
        }

        var newMentions = new List<Mention>();
        var hubSpawnEntityIds = new HashSet<Guid>();
        var existingHubEntityIds = new HashSet<Guid>();

        foreach (var cand in candidates)
        {
            if (note.DeletedAt is not null) break;

            var candEmb = await embeddings.EmbedAsync(cand.CandidateCanonical, ct);
            var neighbors = await EntityVectorQueries.NearestAsync(
                db, cand.CandidateKind, candEmb, o.Pgvector.DedupTopK, ct);

            var surrounding = ExtractSurrounding(body, cand.StartOffset, cand.EndOffset, o.SurroundingTextChars);
            var dedupPrompt = PromptBuilder.BuildDedup(cand, surrounding, neighbors);
            var dedupPid = new PromptId("dedup", "v1");
            var dedupStart = clock.GetCurrentInstant();
            DedupDecisionDto dedup;
            try
            {
                dedup = await llm.CompleteAsync<DedupDecisionDto>(
                    dedupPid, new LlmPromptRequest(dedupPrompt), ct);
            }
            catch (LlmStructuredOutputException ex)
            {
                await events.AppendAsync(job.Id, BuildEvent(LlmEventStages.Dedup, dedupPid, llm, fellBackToSafe,
                    ElapsedMs(dedupStart), ex.Attempts - 1, "failed", null, null, ex.Message), ct);
                continue;
            }
            await events.AppendAsync(job.Id, BuildEvent(LlmEventStages.Dedup, dedupPid, llm, fellBackToSafe,
                ElapsedMs(dedupStart), 0, dedup.Decision, dedup.Confidence, dedup.Rationale, null), ct);

            Entity? target = null;
            if (dedup.Decision == DedupDecisions.AliasOf &&
                dedup.MatchedEntityId is { } mid &&
                dedup.Confidence >= thresholds.DedupAliasMin)
            {
                target = await db.Entities.SingleOrDefaultAsync(
                    e => e.Id == mid && e.DeletedAt == null, ct);
                if (target is not null)
                {
                    var alias = cand.AnchorText?.Trim() ?? "";
                    if (alias.Length > 0 &&
                        !target.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                        !string.Equals(target.CanonicalName, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        target.Aliases = target.Aliases.Append(alias).ToArray();
                    }
                }
            }
            else if (dedup.Decision == DedupDecisions.NewEntity &&
                     dedup.Confidence >= thresholds.DedupNewMin)
            {
                target = new Entity
                {
                    Id = Guid.CreateVersion7(),
                    Kind = cand.CandidateKind,
                    CanonicalName = cand.CandidateCanonical,
                    Aliases = (cand.Aliases ?? Array.Empty<string>()).ToArray(),
                    Source = EntitySource.Llm,
                    IsProvisional = true,
                    Embedding = new Vector(candEmb),
                };
                db.Entities.Add(target);
            }
            // else: ambiguous or below-threshold → drop; provenance captured the call above

            if (target is null) continue;

            target.MentionCount += 1;
            if (target.HubNoteId is null && target.MentionCount >= o.HubMaterializeMin)
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
                CreatedAt = clock.GetCurrentInstant(),
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

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Embedding,
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
