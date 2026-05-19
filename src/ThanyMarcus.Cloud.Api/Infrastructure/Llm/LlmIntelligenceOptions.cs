namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class LlmIntelligenceOptions
{
    public string OllamaTag { get; set; } = "minicpm-v:8b-2.6-q4_K_M";
    public ThresholdsOptions Thresholds { get; init; } = new();
    public PgvectorOptions Pgvector { get; init; } = new();
    public RetryOptions Retry { get; init; } = new();
    public int HubMaterializeMin { get; init; } = 3;
    public int HubMentionWindow { get; init; } = 20;
    public int SurroundingTextChars { get; init; } = 200;
    public int RoutingProjectsMax { get; init; } = 50;
}

public sealed class ThresholdsOptions
{
    public double RouteAcceptMin { get; init; } = 0.5;
    public double MentionMin { get; init; } = 0.6;
    public double DedupAliasMin { get; init; } = 0.8;
    public double DedupNewMin { get; init; } = 0.6;
}

public sealed class PgvectorOptions
{
    public int DedupTopK { get; init; } = 5;
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; init; } = 3;
    public int BackoffSecondsBase { get; init; } = 2;
}
