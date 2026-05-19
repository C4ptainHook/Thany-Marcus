using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public sealed record DedupDecisionDto(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("matched_entity_id")] Guid? MatchedEntityId,
    [property: JsonPropertyName("candidates")] IReadOnlyList<Guid> Candidates,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("rationale")] string Rationale);

public static class DedupDecisions
{
    public const string AliasOf = "alias_of";
    public const string NewEntity = "new_entity";
    public const string Ambiguous = "ambiguous";
}
