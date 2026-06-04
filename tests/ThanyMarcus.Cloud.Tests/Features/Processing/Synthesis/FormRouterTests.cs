using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class FormRouterTests
{
    private static readonly SynthesisInput[] NoInputs = Array.Empty<SynthesisInput>();

    private static EssenceForm Pick(string? body) => FormRouter.Pick(body, NoInputs);

    [Fact]
    public void Plain_prose_picks_prose()
    {
        Pick("Met Mike at the offsite and we agreed on the plan.").ShouldBe(EssenceForm.Prose);
    }

    [Fact]
    public void Empty_input_picks_prose()
    {
        Pick(null).ShouldBe(EssenceForm.Prose);
        Pick("   ").ShouldBe(EssenceForm.Prose);
    }

    [Fact]
    public void Existing_checkboxes_pick_checklist()
    {
        Pick("- [ ] buy milk\n- [x] call Mike").ShouldBe(EssenceForm.Checklist);
    }

    [Fact]
    public void Task_words_pick_checklist()
    {
        Pick("Here is my TODO before the launch.").ShouldBe(EssenceForm.Checklist);
    }

    [Fact]
    public void Checklist_wins_over_plain_bullets()
    {
        Pick("- [ ] one\n- [x] two").ShouldBe(EssenceForm.Checklist);
    }

    [Fact]
    public void Two_or_more_bullet_lines_pick_bullets()
    {
        Pick("- one\n- two\n- three").ShouldBe(EssenceForm.Bullets);
    }

    [Fact]
    public void A_single_bullet_line_stays_prose()
    {
        Pick("- only one point here").ShouldBe(EssenceForm.Prose);
    }

    [Fact]
    public void A_markdown_table_picks_table()
    {
        Pick("| Name | Role |\n| --- | --- |\n| Mike | Eng |").ShouldBe(EssenceForm.Table);
    }

    [Fact]
    public void Signals_in_attachment_content_are_considered()
    {
        var inputs = new[] { new SynthesisInput("file", Content: "- a\n- b\n- c", FailureReason: null) };
        FormRouter.Pick(null, inputs).ShouldBe(EssenceForm.Bullets);
    }
}
