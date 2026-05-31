using System.Text.Json;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Prompts;

public sealed class RouteDecisionDtoTests
{
    [Fact]
    public void Deserializes_with_folder_and_confidence()
    {
        const string json = """{"folder":"Acme","confidence":0.87,"rationale":"matches Acme"}""";
        var dto = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        dto.Folder.ShouldBe("Acme");
        dto.Confidence.ShouldBe(0.87);
        dto.Rationale.ShouldBe("matches Acme");
    }

    [Fact]
    public void Deserializes_with_null_folder()
    {
        const string json = """{"folder":null,"confidence":0.2,"rationale":"no match"}""";
        var dto = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        dto.Folder.ShouldBeNull();
        dto.Confidence.ShouldBe(0.2);
    }

    [Fact]
    public void Roundtrips_to_snake_case_property_names()
    {
        var dto = new RouteDecisionDto(
            Folder: "Acme",
            Confidence: 0.5,
            Rationale: "x");
        var json = JsonSerializer.Serialize(dto);
        json.ShouldContain("\"folder\"");
        json.ShouldContain("\"confidence\"");
        json.ShouldContain("\"rationale\"");
        var back = JsonSerializer.Deserialize<RouteDecisionDto>(json)!;
        back.ShouldBe(dto);
    }
}
