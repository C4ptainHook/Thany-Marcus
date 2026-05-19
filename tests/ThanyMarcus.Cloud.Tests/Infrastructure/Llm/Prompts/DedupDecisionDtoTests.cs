using System.Text.Json;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Prompts;

public sealed class DedupDecisionDtoTests
{
    [Fact]
    public void Deserializes_alias_of()
    {
        const string json = """
            {"decision":"alias_of","matched_entity_id":"9b3c4f12-0000-0000-0000-000000000001",
             "candidates":["9b3c4f12-0000-0000-0000-000000000001"],"confidence":0.85,
             "rationale":"same as existing Acme"}
            """;
        var dto = JsonSerializer.Deserialize<DedupDecisionDto>(json)!;
        dto.Decision.ShouldBe(DedupDecisions.AliasOf);
        dto.MatchedEntityId.ShouldBe(Guid.Parse("9b3c4f12-0000-0000-0000-000000000001"));
        dto.Candidates.Count.ShouldBe(1);
        dto.Confidence.ShouldBe(0.85);
    }

    [Fact]
    public void Deserializes_new_entity()
    {
        const string json = """
            {"decision":"new_entity","matched_entity_id":null,"candidates":[],"confidence":0.9,
             "rationale":"not present"}
            """;
        var dto = JsonSerializer.Deserialize<DedupDecisionDto>(json)!;
        dto.Decision.ShouldBe(DedupDecisions.NewEntity);
        dto.MatchedEntityId.ShouldBeNull();
        dto.Candidates.Count.ShouldBe(0);
    }

    [Fact]
    public void Deserializes_ambiguous_with_candidates_list()
    {
        const string json = """
            {"decision":"ambiguous","matched_entity_id":null,
             "candidates":["9b3c4f12-0000-0000-0000-000000000001","9b3c4f12-0000-0000-0000-000000000002"],
             "confidence":0.3,"rationale":"two equally similar candidates"}
            """;
        var dto = JsonSerializer.Deserialize<DedupDecisionDto>(json)!;
        dto.Decision.ShouldBe(DedupDecisions.Ambiguous);
        dto.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void Roundtrips_to_snake_case()
    {
        var dto = new DedupDecisionDto(
            Decision: DedupDecisions.AliasOf,
            MatchedEntityId: Guid.NewGuid(),
            Candidates: new[] { Guid.NewGuid() },
            Confidence: 0.9,
            Rationale: "r");
        var json = JsonSerializer.Serialize(dto);
        json.ShouldContain("\"matched_entity_id\"");
        json.ShouldContain("\"candidates\"");
        json.ShouldContain("\"decision\":\"alias_of\"");
    }
}
