using System.Text.Json.Nodes;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Synthesis;

public sealed class GeminiSchemaProjectorTests
{
    private static readonly string[] ExpectedOrder = { "title", "tags", "wikilinks", "sentences" };

    private static JsonObject Project(EssenceForm form) =>
        (JsonObject)GeminiSchemaProjector.Project(EssenceSchemas.Build(form, new EssenceBudget(5)));

    [Fact]
    public void Drops_additional_properties_and_max_length()
    {
        var projected = Project(EssenceForm.Prose);
        projected.ContainsKey("additionalProperties").ShouldBeFalse();
        projected["properties"]!["title"]!.AsObject().ContainsKey("maxLength").ShouldBeFalse();
        projected["properties"]!["sentences"]!["items"]!.AsObject().ContainsKey("maxLength").ShouldBeFalse();
    }

    [Fact]
    public void Adds_property_ordering_matching_declaration_order()
    {
        var ordering = Project(EssenceForm.Prose)["propertyOrdering"]!
            .AsArray().Select(n => n!.GetValue<string>()).ToList();
        ordering.ShouldBe(ExpectedOrder);
    }

    [Fact]
    public void Keeps_array_bounds_and_required()
    {
        var projected = Project(EssenceForm.Bullets);
        projected["properties"]!["bullets"]!["maxItems"]!.GetValue<int>().ShouldBe(5);
        ((JsonArray)projected["required"]!).Select(n => n!.GetValue<string>()).ShouldContain("bullets");
    }

    [Fact]
    public void Recurses_into_nested_object_schemas()
    {
        var projected = Project(EssenceForm.Checklist);
        var itemSchema = projected["properties"]!["items"]!["items"]!.AsObject();
        itemSchema.ContainsKey("additionalProperties").ShouldBeFalse();
        itemSchema.ContainsKey("propertyOrdering").ShouldBeTrue();
        itemSchema["properties"]!["text"]!.AsObject().ContainsKey("maxLength").ShouldBeFalse();
    }

    [Fact]
    public void Does_not_mutate_the_logical_schema()
    {
        var logical = EssenceSchemas.Build(EssenceForm.Prose, new EssenceBudget(5));
        _ = GeminiSchemaProjector.Project(logical);
        logical["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        logical.AsObject().ContainsKey("propertyOrdering").ShouldBeFalse();
    }
}
