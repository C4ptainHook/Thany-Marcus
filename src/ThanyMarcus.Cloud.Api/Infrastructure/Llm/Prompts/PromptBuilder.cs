using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public static class PromptBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string BuildRoute(IReadOnlyList<string> folders, string bodyExcerpt)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(bodyExcerpt);
        var folderList = folders.Count == 0
            ? "(none)"
            : string.Join("\n", folders.Select(f => $"- {f}"));
        return string.Format(CultureInfo.InvariantCulture, PromptTemplates.RouteV1Format, folderList, bodyExcerpt);
    }

    public static string BuildExtract(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return string.Format(CultureInfo.InvariantCulture, PromptTemplates.ExtractV1Format, body);
    }

    public static string BuildDedup(
        MentionCandidateDto candidate,
        string surroundingText,
        IReadOnlyList<EntityNeighbor> neighbors)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(surroundingText);
        ArgumentNullException.ThrowIfNull(neighbors);
        var candidateJson = JsonSerializer.Serialize(new
        {
            anchor_text = candidate.AnchorText,
            candidate_kind = candidate.CandidateKind,
            candidate_canonical = candidate.CandidateCanonical,
            aliases = candidate.Aliases,
        }, JsonOpts);
        var neighborsList = neighbors.Count == 0
            ? "(none)"
            : string.Join("\n", neighbors.Select(n =>
            {
                var aliases = n.Aliases is { Count: > 0 } ? $" (aliases: {string.Join(", ", n.Aliases)})" : "";
                var desc = string.IsNullOrWhiteSpace(n.Description) ? "" : $" — {n.Description}";
                return $"- {n.Id} [{n.Kind}] {n.CanonicalName}{aliases}{desc}";
            }));
        return string.Format(CultureInfo.InvariantCulture, PromptTemplates.DedupV1Format,
            candidateJson, surroundingText, neighborsList);
    }

    public static string BuildHubGenerate(
        HubEntityContext entity,
        IReadOnlyList<HubMentionContext> mentions,
        string? previousBody)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mentions);
        var entityBlock = JsonSerializer.Serialize(new
        {
            kind = entity.Kind,
            canonical_name = entity.CanonicalName,
            aliases = entity.Aliases,
        }, JsonOpts);
        var mentionsBlock = mentions.Count == 0
            ? "(no mentions available)"
            : BuildMentionsBlock(mentions);
        var previousBlock = string.IsNullOrEmpty(previousBody)
            ? "Generate the dossier from scratch."
            : $"PREVIOUS DOSSIER (update additively, preserve existing structure):\n{previousBody}";
        return string.Format(CultureInfo.InvariantCulture, PromptTemplates.HubGenerateV1Format,
            entityBlock, mentionsBlock, previousBlock);
    }

    private static string BuildMentionsBlock(IReadOnlyList<HubMentionContext> mentions)
    {
        var sb = new StringBuilder();
        foreach (var m in mentions)
        {
            sb.Append("- ").Append(m.NoteTitle).Append(" @ ").Append(m.NoteCapturedAt).Append('\n');
            sb.Append("  ").Append(m.SurroundingText.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
        }
        return sb.ToString();
    }
}

public sealed record EntityNeighbor(
    Guid Id,
    string Kind,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string? Description);

public sealed record HubMentionContext(string NoteTitle, string NoteCapturedAt, string SurroundingText);

public sealed record HubEntityContext(string Kind, string CanonicalName, IReadOnlyList<string> Aliases);
