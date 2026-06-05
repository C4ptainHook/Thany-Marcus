using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class FolderRouter
{
    public const string InboxFolder = "Inbox";

    private const int BodyExcerptMax = 1500;

    private readonly CloudDbContext db;
    private readonly IClock clock;

    public FolderRouter(CloudDbContext db, IClock clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task<List<string>> LoadCandidatesAsync(int limit, CancellationToken ct)
    {
        var rows = await db.Folders
            .Where(f => f.DeletedAt == null && f.Path != "")
            .Select(f => f.Path)
            .ToListAsync(ct);

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in rows)
        {
            var folder = CandidateSegment(path);
            if (folder is null) continue;
            set.Add(folder);
            if (set.Count >= limit) break;
        }
        return set.Take(limit).ToList();
    }

    public static string? CandidateSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slashIdx = path.IndexOf('/', StringComparison.Ordinal);
        var folder = slashIdx < 0 ? path : path[..slashIdx];
        if (folder.Length == 0) return null;
        if (folder.StartsWith('_') || folder.StartsWith('.')) return null;
        if (string.Equals(folder, InboxFolder, StringComparison.Ordinal)) return null;
        return folder;
    }

    public static string BuildExcerpt(Note note, IReadOnlyList<Attachment> attachments)
    {
        var rawBody = RawExtractions.Concatenate(note, attachments);
        if (string.IsNullOrEmpty(rawBody)) rawBody = note.BodyInput ?? string.Empty;
        return rawBody.Length <= BodyExcerptMax ? rawBody : rawBody[..BodyExcerptMax];
    }

    public async Task<RouteOutcome> DecideAsync(
        string bodyExcerpt,
        IReadOnlyList<string> candidates,
        ILlmClient llm,
        bool fellBackToSafe,
        double routeAcceptMin,
        CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildRoute(candidates, bodyExcerpt);
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
            var failed = new LlmEvent(
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
                Error: ex.Message);
            return new RouteOutcome(null, failed, ex);
        }

        string? chosenFolder = null;
        if (!string.IsNullOrWhiteSpace(decision.Folder) &&
            decision.Confidence >= routeAcceptMin)
        {
            var match = candidates.FirstOrDefault(f =>
                string.Equals(f, decision.Folder, StringComparison.Ordinal));
            if (match is not null) chosenFolder = match;
        }

        var durationMs = (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;
        var evt = new LlmEvent(
            Stage: LlmEventStages.Route,
            PromptId: pid.ToString(),
            Model: llm.ModelName,
            ModelVersion: llm.ModelVersion,
            LlmMode: llm.Mode,
            LlmModeFallback: fellBackToSafe,
            DurationMs: durationMs,
            RetryIndex: 0,
            Decision: chosenFolder is null ? "unrouted" : $"folder:{chosenFolder}",
            Confidence: decision.Confidence,
            Rationale: decision.Rationale,
            Error: null);
        return new RouteOutcome(chosenFolder, evt, null);
    }
}

public sealed record RouteOutcome(
    string? ChosenFolder,
    LlmEvent Event,
    LlmStructuredOutputException? Failure);
