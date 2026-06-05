using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class EssenceRendererTests
{
    private static readonly EssenceBudget Budget = new(5);

    [Fact]
    public void Prose_renders_title_inline_tags_and_a_joined_paragraph()
    {
        var json = """
        {"title":"My Title","tags":["Focus","music and work"],"wikilinks":["Slack"],
         "sentences":["I met [[Mike]].","We discussed [[Slack]]."]}
        """;

        var md = EssenceRenderer.Render(EssenceForm.Prose, json, Budget);

        md.ShouldStartWith("# My Title\n");
        md.ShouldContain("#focus #music-and-work");
        md.ShouldContain("I met [[Mike]]. We discussed [[Slack]].");
    }

    [Fact]
    public void Bullets_render_as_a_dash_list()
    {
        var json = """{"title":"T","tags":[],"wikilinks":[],"bullets":["first","second"]}""";
        var md = EssenceRenderer.Render(EssenceForm.Bullets, json, Budget);
        md.ShouldContain("- first");
        md.ShouldContain("- second");
    }

    [Fact]
    public void Checklist_renders_checked_and_unchecked_boxes()
    {
        var json = """{"title":"T","tags":[],"wikilinks":[],"items":[{"text":"todo","checked":false},{"text":"done","checked":true}]}""";
        var md = EssenceRenderer.Render(EssenceForm.Checklist, json, Budget);
        md.ShouldContain("- [ ] todo");
        md.ShouldContain("- [x] done");
    }

    [Fact]
    public void Table_renders_a_markdown_table_with_header_and_separator()
    {
        var json = """{"title":"T","tags":[],"wikilinks":[],"columns":["Name","Role"],"rows":[{"cells":["Mike","Eng"]},{"cells":["Jo","PM"]}]}""";
        var md = EssenceRenderer.Render(EssenceForm.Table, json, Budget);
        md.ShouldContain("| Name | Role |");
        md.ShouldContain("| --- | --- |");
        md.ShouldContain("| Mike | Eng |");
        md.ShouldContain("| Jo | PM |");
    }

    [Fact]
    public void Overflow_is_compressed_by_dropping_whole_items_not_truncating()
    {
        var json = """{"title":"T","tags":[],"wikilinks":[],"bullets":["a","b","c","d"]}""";
        var md = EssenceRenderer.Render(EssenceForm.Bullets, json, new EssenceBudget(2));
        md.ShouldContain("- a");
        md.ShouldContain("- b");
        md.ShouldNotContain("- c");
        md.ShouldNotContain("- d");
    }

    [Fact]
    public void Empty_title_falls_back_without_throwing()
    {
        var json = """{"title":"","tags":[],"wikilinks":[],"sentences":["hi."]}""";
        EssenceRenderer.Render(EssenceForm.Prose, json, Budget).ShouldStartWith("# Untitled note");
    }

    [Fact]
    public void Malformed_json_throws_essence_parse_exception()
    {
        Should.Throw<EssenceParseException>(() =>
            EssenceRenderer.Render(EssenceForm.Prose, "{ not json", Budget));
    }

    [Fact]
    public void Empty_content_array_throws_essence_parse_exception()
    {
        var json = """{"title":"T","tags":[],"wikilinks":[],"sentences":[]}""";
        Should.Throw<EssenceParseException>(() =>
            EssenceRenderer.Render(EssenceForm.Prose, json, Budget));
    }

    [Fact]
    public void Origin_link_free_invariant_is_a_renderer_concern_only_for_the_essence()
    {
        var json = """{"title":"T","tags":["x"],"wikilinks":["Slack"],"sentences":["A [[Slack]] thought."]}""";
        var md = EssenceRenderer.Render(EssenceForm.Prose, json, Budget);
        md.ShouldContain("[[Slack]]");
    }

    [Fact]
    public void Prose_brackets_array_targets_present_in_clean_prose()
    {
        var json = """
        {"title":"T","tags":["x"],"wikilinks":["Thany-Marcus"],
         "sentences":["I joined the Thany-Marcus team today."]}
        """;
        var md = EssenceRenderer.Render(EssenceForm.Prose, json, Budget);
        md.ShouldContain("I joined the [[Thany-Marcus]] team today.");
        md.ShouldNotContain("Related:");
    }

    [Fact]
    public void Prose_array_target_absent_from_prose_surfaces_in_a_related_line()
    {
        var json = """
        {"title":"T","tags":["x"],"wikilinks":["Customer Success"],
         "sentences":["We talked about churn."]}
        """;
        var md = EssenceRenderer.Render(EssenceForm.Prose, json, Budget);
        md.ShouldContain("We talked about churn.");
        md.ShouldEndWith("Related: [[Customer Success]]");
    }

    [Fact]
    public void Table_uses_fallback_line_only_and_leaves_the_table_intact()
    {
        var json = """{"title":"T","tags":[],"wikilinks":["Mike"],"columns":["Name","Role"],"rows":[{"cells":["Mike","Eng"]}]}""";
        var md = EssenceRenderer.Render(EssenceForm.Table, json, Budget);

        md.ShouldContain("| Name | Role |");
        md.ShouldContain("| Mike | Eng |");
        md.ShouldNotContain("[[Mike]] |");
        md.ShouldEndWith("Related: [[Mike]]");
    }
}
