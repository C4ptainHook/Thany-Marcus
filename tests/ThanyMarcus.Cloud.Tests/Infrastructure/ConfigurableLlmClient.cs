using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class ConfigurableLlmClient : ILlmClient
{
    public string Mode { get; set; } = LlmModes.Safe;
    public string ModelName { get; set; } = "stub";
    public string ModelVersion { get; set; } = "stub-v1";

    public Func<RouteDecisionDto>? RouteResponse { get; set; }
    public Func<EntityExtractionDto>? ExtractResponse { get; set; }
    public Func<DedupDecisionDto>? DedupResponse { get; set; }
    public Func<string>? HubGenerateResponse { get; set; }
    public List<PromptId> Calls { get; } = new();

    public Task<T> CompleteAsync<T>(PromptId promptId, object inputContext, CancellationToken ct)
        where T : class
    {
        Calls.Add(promptId);
        object payload = promptId.Name switch
        {
            "route" => RouteResponse?.Invoke()
                ?? new RouteDecisionDto(null, 0.0, "stub"),
            "extract" => ExtractResponse?.Invoke()
                ?? new EntityExtractionDto(Array.Empty<MentionCandidateDto>()),
            "dedup" => DedupResponse?.Invoke()
                ?? new DedupDecisionDto(DedupDecisions.Ambiguous, null, Array.Empty<Guid>(), 0.0, "stub"),
            "hub-generate" => HubGenerateResponse?.Invoke() ?? "### Context\n\n- stub hub body\n",
            _ => throw new InvalidOperationException($"unhandled prompt: {promptId}"),
        };
        return Task.FromResult((T)payload);
    }
}

public sealed class ConfigurableLlmClientFactory : ILlmClientFactory
{
    public ConfigurableLlmClient Client { get; }
    public bool FallbackToSafe { get; set; }

    public ConfigurableLlmClientFactory(ConfigurableLlmClient client) { Client = client; }

    public ILlmClient Resolve(CloudSettings settings, out bool fallbackToSafe)
    {
        fallbackToSafe = FallbackToSafe;
        return Client;
    }
}
