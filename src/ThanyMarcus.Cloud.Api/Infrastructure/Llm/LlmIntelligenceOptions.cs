namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class LlmIntelligenceOptions
{
    public ThresholdsOptions Thresholds { get; init; } = new();
    public PgvectorOptions Pgvector { get; init; } = new();
    public RetryOptions Retry { get; init; } = new();
    public EntitySuggestionsOptions EntitySuggestions { get; init; } = new();
    public int HubMaterializeMin { get; init; } = 3;
    public int HubMentionWindow { get; init; } = 20;
    public int SurroundingTextChars { get; init; } = 200;
    public int RoutingProjectsMax { get; init; } = 50;
}

public sealed class ThresholdsOptions
{
    public double RouteAcceptMin { get; init; } = 0.5;
    public double MentionMin { get; init; } = 0.6;

    // Max cosine distance for a candidate mention to count as a confident match against an
    // existing entity (curated) or an open suggestion. Replaces the dedup-LLM's alias_of decision.
    public double SuggestionMatchDistance { get; init; } = 0.25;
}

public sealed class EntitySuggestionsOptions
{
    public int OccurrenceThreshold { get; init; } = 3;
    public int DistinctNoteThreshold { get; init; } = 2;
    public string StubsFolder { get; init; } = "Entities";
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
