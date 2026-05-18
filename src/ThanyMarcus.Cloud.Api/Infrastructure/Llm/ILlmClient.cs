namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public interface ILlmClient
{
    string Mode { get; }
    string ModelName { get; }
    Task<CompositeEnrichmentResult> EnrichCompositeAsync(
        CompositeEnrichmentRequest request,
        CancellationToken ct);
}

public sealed record CompositeEnrichmentRequest(
    string Body,
    IReadOnlyList<AttachmentText> Attachments);

public sealed record AttachmentText(Guid AttachmentId, string Kind, string ExtractedText);

public sealed record CompositeEnrichmentResult(
    string? SuggestedProject,
    IReadOnlyList<WikilinkAnchor> BodyAnchors,
    IReadOnlyList<AttachmentAnchors> AttachmentAnchors,
    IReadOnlyList<string> Tags,
    int? InputTokens,
    int? OutputTokens);

public sealed record WikilinkAnchor(string Text, int Start, int End, string Target);

public sealed record AttachmentAnchors(Guid AttachmentId, IReadOnlyList<WikilinkAnchor> Anchors);
