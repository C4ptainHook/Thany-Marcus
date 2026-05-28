using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class RoutingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Routing;

    private const int BodyExcerptMax = 1500;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly LlmEventAppender events;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;

    public RoutingHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        LlmEventAppender events,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock,
        JobStateTransitions transitions)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.events = events;
        this.opts = opts;
        this.clock = clock;
        this.transitions = transitions;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);

        // FORK: hub-regen notes don't get routed to a project — they live under _Entities/.
        // Hub flow skips synthesis entirely and goes straight to embedding.
        if (note.IsHub)
        {
            await transitions.TransitionAsync(
                job,
                nextStatus: IngestJobStatus.Embedding,
                lastError: null,
                clearLease: true,
                setFinishedAt: false,
                ct);
            return PhaseHandlerResult.Advanced;
        }

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var llm = llmFactory.Resolve(settings, out var fellBackToSafe);
        var o = opts.CurrentValue;

        var projects = await db.Entities
            .Where(e => e.Kind == EntityKind.Project && e.DeletedAt == null)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(o.RoutingProjectsMax)
            .Select(e => new ProjectListItem(e.Id, e.CanonicalName, e.Description))
            .ToListAsync(ct);

        var attachments = await db.Attachments
            .Where(a => a.NoteId == note.Id)
            .ToListAsync(ct);
        var rawBody = RawExtractions.Concatenate(note, attachments);
        if (string.IsNullOrEmpty(rawBody)) rawBody = note.BodyInput ?? string.Empty;
        var bodyExcerpt = Truncate(rawBody, BodyExcerptMax);
        var prompt = PromptBuilder.BuildRoute(projects, bodyExcerpt);

        var pid = new PromptId("route", "v1");
        var start = clock.GetCurrentInstant();
        RouteDecisionDto decision;
        try
        {
            decision = await llm.CompleteAsync<RouteDecisionDto>(pid, new LlmPromptRequest(prompt), ct);
        }
        catch (LlmStructuredOutputException ex)
        {
            var elapsed = (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;
            await events.AppendAsync(job.Id, new LlmEvent(
                Stage: LlmEventStages.Route,
                PromptId: pid.ToString(),
                Model: llm.ModelName,
                ModelVersion: llm.ModelVersion,
                LlmMode: llm.Mode,
                LlmModeFallback: fellBackToSafe,
                DurationMs: elapsed,
                RetryIndex: ex.Attempts - 1,
                Decision: "failed",
                Confidence: null,
                Rationale: null,
                Error: ex.Message), ct);
            throw;
        }

        note.LlmMode = llm.Mode;
        if (decision.ProjectEntityId is { } projectId &&
            decision.Confidence >= o.Thresholds.RouteAcceptMin)
        {
            var project = await db.Entities.SingleOrDefaultAsync(
                e => e.Id == projectId && e.Kind == EntityKind.Project && e.DeletedAt == null,
                ct);
            if (project is not null)
            {
                note.ProjectId = project.Id;
                note.RelativePath = $"Projects/{project.CanonicalName}/{note.Id}.md";
                project.UpdatedAt = clock.GetCurrentInstant();
            }
        }

        var durationMs = (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;
        await events.AppendAsync(job.Id, new LlmEvent(
            Stage: LlmEventStages.Route,
            PromptId: pid.ToString(),
            Model: llm.ModelName,
            ModelVersion: llm.ModelVersion,
            LlmMode: llm.Mode,
            LlmModeFallback: fellBackToSafe,
            DurationMs: durationMs,
            RetryIndex: 0,
            Decision: note.ProjectId is null ? "unrouted" : $"project:{note.ProjectId}",
            Confidence: decision.Confidence,
            Rationale: decision.Rationale,
            Error: null), ct);

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Synthesizing,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
