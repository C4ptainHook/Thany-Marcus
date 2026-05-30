using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static partial class RelatedNotesEndpoint
{
    public static void MapRelatedNotesEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/notes/related", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("PostNotesRelated")
            .Produces<RelatedNotesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    public sealed record RelatedNotesRequest(string? Body, Guid? NoteId, int? K);

    public sealed record RelatedNotesItem(
        Guid Id,
        string RelativePath,
        string Title,
        string Snippet,
        double Distance);

    public sealed record RelatedNotesResponse(IReadOnlyList<RelatedNotesItem> Items);

    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex H1Title();

    [GeneratedRegex(@"^---\s*\r?\n.*?\r?\n---\s*\r?\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    private static async Task<IResult> HandleAsync(
        RelatedNotesRequest req,
        CloudDbContext db,
        IEmbeddingClient embeddings,
        QueryEmbeddingCache cache,
        IClock clock,
        IOptions<RelatedNotesOptions> opts,
        CancellationToken ct)
    {
        var o = opts.Value;
        var hasBody = !string.IsNullOrWhiteSpace(req.Body);
        var hasNoteId = req.NoteId.HasValue;
        if (hasBody == hasNoteId)
        {
            return Results.Problem(
                detail: "Provide exactly one of body or noteId.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var k = Math.Clamp(req.K ?? o.DefaultK, 1, o.MaxK);

        float[] queryVec;
        Guid? excludeId = null;

        if (hasBody)
        {
            var body = req.Body!;
            if (body.Length < o.MinBodyChars)
            {
                return Results.Problem(
                    detail: $"Body must be at least {o.MinBodyChars} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            queryVec = await cache.GetOrAddAsync(body, embeddings, ct);
        }
        else
        {
            var noteId = req.NoteId!.Value;
            var stored = await db.Notes
                .Where(n => n.Id == noteId && n.DeletedAt == null)
                .Select(n => new { n.Embedding })
                .SingleOrDefaultAsync(ct);
            if (stored is null || stored.Embedding is null)
            {
                return Results.NotFound();
            }
            queryVec = stored.Embedding.ToArray();
            excludeId = noteId;
        }

        var now = clock.GetCurrentInstant();
        var excludeAfter = now - Duration.FromHours(o.ExcludeRecentHours);

        var neighbors = await NoteVectorQueries.NearestAsync(
            db, queryVec, k, excludeId, excludeAfter, ct);

        var items = neighbors
            .Where(n => n.Distance <= o.MaxDistance)
            .Select(n => new RelatedNotesItem(
                Id: n.Id,
                RelativePath: n.RelativePath,
                Title: ExtractTitle(n.BodyOutput, n.RelativePath),
                Snippet: ExtractSnippet(n.BodyOutput),
                Distance: n.Distance))
            .ToList();

        return Results.Ok(new RelatedNotesResponse(items));
    }

    internal static string ExtractTitle(string bodyOutput, string relativePath)
    {
        var stripped = Frontmatter().Replace(bodyOutput ?? string.Empty, string.Empty);
        var match = H1Title().Match(stripped);
        if (match.Success)
        {
            var title = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(title)) return title;
        }
        if (!string.IsNullOrEmpty(relativePath))
        {
            var name = Path.GetFileNameWithoutExtension(relativePath);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return string.Empty;
    }

    internal static string ExtractSnippet(string bodyOutput)
    {
        if (string.IsNullOrEmpty(bodyOutput)) return string.Empty;
        var stripped = Frontmatter().Replace(bodyOutput, string.Empty);
        var lines = stripped.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith('#')) continue;
            return line.Length > 140 ? line[..140].TrimEnd() + "…" : line;
        }
        return string.Empty;
    }
}
