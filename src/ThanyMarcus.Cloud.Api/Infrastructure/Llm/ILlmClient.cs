namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public interface ILlmClient
{
    string Mode { get; }
    string ModelName { get; }
    string ModelVersion { get; }
    Task<T> CompleteAsync<T>(
        PromptId promptId,
        object inputContext,
        CancellationToken ct) where T : class;
}

public sealed record PromptId(string Name, string Version)
{
    public override string ToString() => $"{Name}-{Version}";
}

public sealed class LlmStructuredOutputException : Exception
{
    public PromptId PromptId { get; }
    public int Attempts { get; }

    public LlmStructuredOutputException(PromptId pid, int attempts, Exception? inner = null)
        : base($"LLM output failed to parse after {attempts} attempts for prompt {pid}", inner)
    {
        PromptId = pid;
        Attempts = attempts;
    }
}

public interface IUnsafeLlmClient : ILlmClient { }
