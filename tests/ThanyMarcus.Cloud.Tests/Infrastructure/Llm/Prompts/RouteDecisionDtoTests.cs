using System.Text.Json;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Prompts;

public sealed class RouteDecisionDtoTests
{
    [Fact]
    public void Deserializes_with_project_id_and_confidence()
    {
        const string json = """{"project_entity_id":"9b3c4f12-0000-0000-0000-000000000001","confidence":0.87,"rationale":"matches Acme"}""";
        var dto = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        dto.ProjectEntityId.ShouldBe(Guid.Parse("9b3c4f12-0000-0000-0000-000000000001"));
        dto.Confidence.ShouldBe(0.87);
        dto.Rationale.ShouldBe("matches Acme");
    }

    [Fact]
    public void Deserializes_with_null_project_id()
    {
        const string json = """{"project_entity_id":null,"confidence":0.2,"rationale":"no match"}""";
        var dto = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        dto.ProjectEntityId.ShouldBeNull();
        dto.Confidence.ShouldBe(0.2);
    }

    [Fact]
    public void Roundtrips_to_snake_case_property_names()
    {
        var dto = new RouteDecisionDto(
            ProjectEntityId: Guid.Parse("9b3c4f12-0000-0000-0000-000000000001"),
            Confidence: 0.5,
            Rationale: "x");
        var json = JsonSerializer.Serialize(dto);
        json.ShouldContain("\"project_entity_id\"");
        json.ShouldContain("\"confidence\"");
        json.ShouldContain("\"rationale\"");
        var back = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        back.ShouldBe(dto);
    }
}
