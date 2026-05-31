using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static partial class ListJobsEndpoint
{
    private const int RecentLimit = 10;
    private const int BodyPrefixChars = 60;
    private const string UntitledTitle = "(untitled)";

    [GeneratedRegex(@"^---\s*\r?\n.*?\r?\n---\s*\r?\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex H1Title();

    public static void MapListJobsEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/ingest/jobs", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("GetIngestJobs")
            .Produces<ListJobsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    public sealed record ListJobsItem(
        Guid NoteId,
        string Title,
        string Status,
        int AttemptCount,
        string? Error,
        string? VaultPath,
        IReadOnlyList<ExtractionFailureItem> ExtractionFailures);

    public sealed record ExtractionFailureItem(string Kind, string Reason);

    public sealed record ListJobsResponse(
        IReadOnlyList<ListJobsItem> Active,
        IReadOnlyList<ListJobsItem> Recent);

    private static async Task<IResult> HandleAsync(
        [FromQuery] string? status,
        [FromQuery] string? include,
        CloudDbContext db,
        CancellationToken ct)
    {
        if (status is not null && !string.Equals(status, "active", StringComparison.Ordinal))
        {
            return Results.Problem(
                $"unsupported status filter '{status}' (only 'active' is supported)",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var includeRecent = string.Equals(include, "recent", StringComparison.Ordinal);
        var terminalStatuses = IngestJobStatus.Terminals.ToArray();

        var activeRows = await db.IngestJobs
            .Where(j => !terminalStatuses.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .Join(db.Notes, j => j.NoteId, n => n.Id, (j, n) => new JobNoteRow(j, n))
            .ToListAsync(ct);

        var recentRows = includeRecent
            ? await db.IngestJobs
                .Where(j => terminalStatuses.Contains(j.Status))
                .OrderByDescending(j => j.UpdatedAt)
                .Take(RecentLimit)
                .Join(db.Notes, j => j.NoteId, n => n.Id, (j, n) => new JobNoteRow(j, n))
                .ToListAsync(ct)
            : new List<JobNoteRow>();

        var noteIds = activeRows.Select(r => r.Note.Id)
            .Concat(recentRows.Select(r => r.Note.Id))
            .Distinct()
            .ToList();

        var failuresByNote = noteIds.Count == 0
            ? new Dictionary<Guid, List<ExtractionFailureItem>>()
            : (await db.Attachments
                .Where(a => noteIds.Contains(a.NoteId)
                            && a.ExtractionStatus == AttachmentExtractionStatus.Failed)
                .Select(a => new { a.NoteId, a.Kind, a.ExtractionError })
                .ToListAsync(ct))
                .GroupBy(a => a.NoteId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new ExtractionFailureItem(x.Kind, x.ExtractionError ?? "unknown")).ToList());

        var active = activeRows.Select(r => ToItem(r, failuresByNote)).ToList();
        var recent = recentRows.Select(r => ToItem(r, failuresByNote)).ToList();

        return Results.Ok(new ListJobsResponse(active, recent));
    }

    private static ListJobsItem ToItem(
        JobNoteRow row,
        Dictionary<Guid, List<ExtractionFailureItem>> failuresByNote)
    {
        var failures = failuresByNote.TryGetValue(row.Note.Id, out var f)
            ? (IReadOnlyList<ExtractionFailureItem>)f
            : Array.Empty<ExtractionFailureItem>();
        return new ListJobsItem(
            NoteId: row.Note.Id,
            Title: ExtractTitle(row.Note.BodyOutput, row.Note.BodyInput),
            Status: row.Job.Status,
            AttemptCount: row.Job.Attempts,
            Error: row.Job.LastError,
            VaultPath: row.Note.RelativePath,
            ExtractionFailures: failures);
    }

    internal static string ExtractTitle(string? bodyOutput, string? bodyInput)
    {
        if (!string.IsNullOrEmpty(bodyOutput))
        {
            var stripped = Frontmatter().Replace(bodyOutput, string.Empty);
            var match = H1Title().Match(stripped);
            if (match.Success)
            {
                var title = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(title)) return title;
            }
        }
        if (!string.IsNullOrWhiteSpace(bodyInput))
        {
            var trimmed = bodyInput.Trim();
            return trimmed.Length > BodyPrefixChars
                ? trimmed.Substring(0, BodyPrefixChars).TrimEnd()
                : trimmed;
        }
        return UntitledTitle;
    }

    private sealed record JobNoteRow(IngestJob Job, Note Note);
}
