using System.Text.Json.Nodes;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public sealed record SynthesisRequest(
    string Prompt,
    string Model,
    string? ApiKey,
    int Seed,
    double Temperature,
    int MaxOutputTokens,
    JsonNode? ResponseSchema = null);

public sealed record SynthesisResponse(
    string Body,
    string ModelTag,
    int PromptTokens,
    int CompletionTokens);

public sealed class SynthesisLlmException : Exception
{
    public SynthesisLlmException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ISynthesisLlmClient
{
    string ProviderId { get; }
    Task<SynthesisResponse> CompleteAsync(SynthesisRequest req, CancellationToken ct);
}
