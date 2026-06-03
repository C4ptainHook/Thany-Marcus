using System.Text.Json.Nodes;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class EssenceSchemasTests
{
    private static readonly EssenceBudget Budget = new(5);

    [Fact]
    public void Every_form_shares_the_head_fields()
    {
        foreach (var form in Enum.GetValues<EssenceForm>())
        {
            var props = (JsonObject)EssenceSchemas.Build(form, Budget)["properties"]!;
            props.ContainsKey("title").ShouldBeTrue();
            props.ContainsKey("tags").ShouldBeTrue();
            props.ContainsKey("wikilinks").ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(EssenceForm.Prose, "sentences")]
    [InlineData(EssenceForm.Bullets, "bullets")]
    [InlineData(EssenceForm.Checklist, "items")]
    public void Each_form_carries_its_content_field_with_the_unit_ceiling(EssenceForm form, string field)
    {
        var schema = EssenceSchemas.Build(form, Budget);
        var content = (JsonObject)schema["properties"]![field]!;

        content["maxItems"]!.GetValue<int>().ShouldBe(Budget.Units);
        ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ShouldContain(field);
    }

    [Fact]
    public void Table_form_carries_columns_and_rows()
    {
        var schema = EssenceSchemas.Build(EssenceForm.Table, Budget);
        var props = (JsonObject)schema["properties"]!;
        props.ContainsKey("columns").ShouldBeTrue();
        props["rows"]!["maxItems"]!.GetValue<int>().ShouldBe(Budget.Units);
    }

    [Fact]
    public void Schemas_forbid_additional_properties()
    {
        EssenceSchemas.Build(EssenceForm.Prose, Budget)["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
    }
}
