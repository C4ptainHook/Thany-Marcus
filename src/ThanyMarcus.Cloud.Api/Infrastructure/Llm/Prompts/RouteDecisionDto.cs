using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public sealed record RouteDecisionDto(
    [property: JsonPropertyName("project_entity_id")] Guid? ProjectEntityId,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("rationale")] string Rationale);
