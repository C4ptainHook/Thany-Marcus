using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class WikilinkWeaverTests
{
    private const int Max = 12;

    private static string Weave(string body, params string[] targets) =>
        WikilinkWeaver.Weave(body, targets, Max);

    private static string Related(string body, params string[] targets) =>
        WikilinkWeaver.AppendRelated(body, targets, Max);

    [Fact]
    public void First_occurrence_is_bracketed_later_ones_left_untouched()
    {
        var woven = Weave("I use Slack daily and Slack again at night.", "Slack");
        woven.ShouldBe("I use [[Slack]] daily and Slack again at night.");
    }

    [Fact]
    public void Target_absent_from_prose_appears_only_in_the_fallback_line()
    {
        var woven = Weave("A note with no matching word.", "Thany-Marcus");
        woven.ShouldBe("A note with no matching word.\n\nRelated: [[Thany-Marcus]]");
    }

    [Fact]
    public void Already_inlined_target_is_neither_double_bracketed_nor_duplicated_in_fallback()
    {
        var woven = Weave("We shipped on [[Slack]] this week.", "Slack");
        woven.ShouldBe("We shipped on [[Slack]] this week.");
    }

    [Fact]
    public void Model_aliased_link_satisfies_the_array_target_without_duplication()
    {
        var woven = Weave("Spoke to [[Michael Jackson|Mike]] today.", "Michael Jackson");
        woven.ShouldBe("Spoke to [[Michael Jackson|Mike]] today.");
    }

    [Fact]
    public void Substring_is_not_bracketed_inside_a_longer_word()
    {
        var woven = Weave("I love note-taking systems.", "note-taking");
        woven.ShouldBe("I love [[note-taking]] systems.");
    }

    [Fact]
    public void Longer_overlapping_target_wins_and_claims_the_span()
    {
        var woven = Weave("I love note-taking systems.", "note", "note-taking");

        woven.ShouldContain("[[note-taking]]");
        woven.ShouldNotContain("[[note]]-taking");
        woven.ShouldNotContain("[[note]]-[[taking]]");
        woven.ShouldContain("Related: [[note]]");
    }

    [Fact]
    public void Cyrillic_target_brackets_on_unicode_word_boundaries()
    {
        var woven = Weave("Поїздка до Київ завтра вранці.", "Київ");
        woven.ShouldBe("Поїздка до [[Київ]] завтра вранці.");
    }

    [Fact]
    public void Regex_metachar_target_is_matched_literally_not_as_a_pattern()
    {
        var woven = Weave("We chose C++ for the core.", "C++");
        woven.ShouldBe("We chose [[C++]] for the core.");
    }

    [Fact]
    public void Regex_metachar_target_does_not_match_arbitrary_characters()
    {
        var woven = Weave("We chose Cxx for the core.", "C..");
        woven.ShouldBe("We chose Cxx for the core.\n\nRelated: [[C..]]");
    }

    [Fact]
    public void Targets_are_clamped_to_max()
    {
        var targets = Enumerable.Range(1, 20).Select(i => $"T{i}").ToArray();
        var woven = WikilinkWeaver.Weave("no matches here", targets, max: 3);

        woven.ShouldBe("no matches here\n\nRelated: [[T1]] · [[T2]] · [[T3]]");
    }

    [Fact]
    public void Empty_and_whitespace_targets_are_dropped()
    {
        var woven = Weave("body text", "", "  ", "[]", "#");
        woven.ShouldBe("body text");
    }

    [Fact]
    public void Bracket_and_hash_noise_is_stripped_from_targets()
    {
        var woven = Weave("no match", "[[Slack]]", "#focus");
        woven.ShouldBe("no match\n\nRelated: [[Slack]] · [[focus]]");
    }

    [Fact]
    public void Case_insensitive_dedupe_keeps_first_seen_casing()
    {
        var woven = Weave("no match", "Slack", "slack", "SLACK");
        woven.ShouldBe("no match\n\nRelated: [[Slack]]");
    }

    [Fact]
    public void Inline_match_preserves_the_bodys_casing_not_the_array_casing()
    {
        var woven = Weave("Met the Thany-Marcus team.", "thany-marcus");
        woven.ShouldBe("Met the [[Thany-Marcus]] team.");
    }

    [Fact]
    public void Mixed_some_inlined_some_fallback()
    {
        var woven = Weave("I shipped Slack but not the rest.", "Slack", "Roadmap");
        woven.ShouldBe("I shipped [[Slack]] but not the rest.\n\nRelated: [[Roadmap]]");
    }

    [Fact]
    public void Null_or_empty_targets_leave_the_body_unchanged()
    {
        WikilinkWeaver.Weave("hello", null, Max).ShouldBe("hello");
        WikilinkWeaver.Weave("hello", Array.Empty<string>(), Max).ShouldBe("hello");
    }

    [Fact]
    public void Append_related_does_no_inline_weaving_and_keeps_table_markdown_intact()
    {
        var table = "| Name | Role |\n| --- | --- |\n| Mike | Eng |";
        var woven = Related(table, "Mike", "Eng");

        woven.ShouldStartWith(table);
        woven.ShouldEndWith("\n\nRelated: [[Mike]] · [[Eng]]");
        woven.ShouldNotContain("[[Mike]] |");
        woven.ShouldNotContain("| [[");
    }

    [Fact]
    public void Append_related_skips_targets_already_linked_in_the_table()
    {
        var table = "| Name | Role |\n| --- | --- |\n| [[Mike]] | Eng |";
        var woven = Related(table, "Mike", "Eng");
        woven.ShouldEndWith("\n\nRelated: [[Eng]]");
    }
}
