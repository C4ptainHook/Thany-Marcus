namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class NoOpLlmClient : ILlmClient
{
    public string Mode      => Features.Settings.LlmModes.Safe;
    public string ModelName => "noop";

    public Task<CompositeEnrichmentResult> EnrichCompositeAsync(
        CompositeEnrichmentRequest request, CancellationToken ct) =>
        Task.FromResult(new CompositeEnrichmentResult(
            SuggestedProject:  null,
            BodyAnchors:       Array.Empty<WikilinkAnchor>(),
            AttachmentAnchors: Array.Empty<AttachmentAnchors>(),
            Tags:              Array.Empty<string>(),
            InputTokens:       null,
            OutputTokens:      null));
}
