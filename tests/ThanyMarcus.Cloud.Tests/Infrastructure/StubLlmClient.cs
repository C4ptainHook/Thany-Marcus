using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class StubLlmClient : ILlmClient
{
    public string Mode => LlmModes.Safe;
    public string ModelName => "stub";
    public string ModelVersion => "stub-v1";

    public Task<T> CompleteAsync<T>(PromptId promptId, object inputContext, CancellationToken ct)
        where T : class
    {
        object payload = promptId.Name switch
        {
            "route" => new RouteDecisionDto(Folder: null, Confidence: 0.0, Rationale: "stub"),
            "extract" => new EntityExtractionDto(Mentions: Array.Empty<MentionCandidateDto>()),
            "dedup" => new DedupDecisionDto(
                Decision: DedupDecisions.Ambiguous,
                MatchedEntityId: null,
                Candidates: Array.Empty<Guid>(),
                Confidence: 0.0,
                Rationale: "stub"),
            "hub-generate" => "### Context\n\n- stub hub body\n",
            _ => throw new InvalidOperationException($"unhandled prompt: {promptId}"),
        };
        return Task.FromResult((T)payload);
    }
}

public sealed class StubLlmClientFactory : ILlmClientFactory
{
    private readonly ILlmClient client;
    public StubLlmClientFactory(ILlmClient client) { this.client = client; }
    public ILlmClient Resolve(CloudSettings settings, out bool fallbackToSafe)
    {
        fallbackToSafe = false;
        return client;
    }
}
