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
    public int RoutingFoldersMax { get; init; } = 50;
    public int RerouteMaxBatch { get; init; } = 200;
    public int RerouteCooldownSeconds { get; init; } = 30;

    // How many sample mention contexts to fold into an entity/candidate embedding alongside the
    // name. Context is what carries the cross-lingual signal (Київ↔Kyiv) a bare name lacks.
    public int EmbeddingContextSamples { get; init; } = 3;

    // Margin a challenger surface form must exceed the current DisplayName's mention count by
    // before the label flips. Decoupled from CanonicalName, so flipping moves nothing in the graph.
    public int DisplayNameHysteresisMargin { get; init; } = 2;
}

public sealed class ThresholdsOptions
{
    public double RouteAcceptMin { get; init; } = 0.5;
    public double MentionMin { get; init; } = 0.6;

    // Max cosine distance for a candidate mention to count as a confident match against an
    // existing entity (curated) or an open suggestion. Replaces the dedup-LLM's alias_of decision.
    public double SuggestionMatchDistance { get; init; } = 0.11;

    // Upper bound of the gray band: a candidate landing between SuggestionMatchDistance and this
    // is too far to auto-merge but near enough to propose a cross-language merge for user confirm.
    public double GrayZoneMergeMaxDistance { get; init; } = 0.18;
}

public sealed class EntitySuggestionsOptions
{
    public int OccurrenceThreshold { get; init; } = 3;
    public int DistinctNoteThreshold { get; init; } = 2;
    public string StubsFolder { get; init; } = "_Entities/Stubs";
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
