using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class SynthesisPromptBuilderTests
{
    private static readonly SynthesisInput[] NoInputs = Array.Empty<SynthesisInput>();
    private static readonly EssenceBudget Budget = new(5);

    private static string Build(string systemBody, string? userBody, SynthesisInput[] inputs,
        EssenceForm form = EssenceForm.Prose) =>
        SynthesisPromptBuilder.Build(systemBody, userBody, inputs, form, Budget);

    [Fact]
    public void Reference_attachments_absent_from_prompt()
    {
        var systemBody = SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten);
        var withReference = Build(systemBody, "note body",
            new[] { new SynthesisInput("voice", Content: null, FailureReason: null,
                Mode: AttachmentMode.Reference, Id: "att-1") });
        var withNoInputs = Build(systemBody, "note body", NoInputs);

        withReference.ShouldBe(withNoInputs);
        withReference.ShouldNotContain("att-1");
    }

    [Fact]
    public void Metadata_attachments_emit_reference_block_not_input_block()
    {
        var inputs = new[]
        {
            new SynthesisInput("url", Content: null, FailureReason: null,
                Mode: AttachmentMode.Metadata, Id: "att-2",
                Title: "Some Page", Description: "A blurb", Url: "https://example.com/p"),
        };

        var prompt = Build(SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten), "note body", inputs);

        prompt.ShouldContain("<reference id=\"att-2\" title=\"Some Page\" description=\"A blurb\" url=\"https://example.com/p\"/>");
        prompt.ShouldNotContain("URL extract:");
    }

    [Fact]
    public void Build_instructs_a_single_json_object_and_keeps_the_user_text()
    {
        var prompt = Build(
            SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten),
            "Met Mike at the Slack offsite.",
            NoInputs);

        prompt.ShouldNotContain("Available entities");
        prompt.ShouldContain("Met Mike at the Slack offsite.");
        prompt.ShouldContain("Respond with a single JSON object");
        prompt.ShouldContain("/no_think");
    }

    [Theory]
    [InlineData(EssenceForm.Prose, "\"sentences\"")]
    [InlineData(EssenceForm.Bullets, "\"bullets\"")]
    [InlineData(EssenceForm.Checklist, "\"items\"")]
    [InlineData(EssenceForm.Table, "\"rows\"")]
    public void Output_contract_names_the_chosen_form_field(EssenceForm form, string expectedField)
    {
        var prompt = Build(SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten), "x", NoInputs, form);
        prompt.ShouldContain(expectedField);
    }

    [Fact]
    public void Guardrails_use_judgment_based_wikilink_language_not_a_canonical_list()
    {
        var body = SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten);
        body.ShouldContain("[[ ]]");
        body.ShouldContain("your judgment");
        body.ShouldNotContain("canonical entity list");
        body.ShouldNotContain("Only names from");
    }

    [Theory]
    [InlineData(SynthesisPresets.Zettelkasten, "preset-zettelkasten-v3")]
    [InlineData(SynthesisPresets.Journal, "preset-journal-v3")]
    [InlineData(SynthesisPresets.Encyclopedic, "preset-encyclopedic-v3")]
    [InlineData(SynthesisPresets.Technical, "preset-technical-v3")]
    public void Preset_versions_are_bumped_to_v3(string preset, string expectedVersion)
    {
        SynthesisPresetBodies.VersionFor(preset).ShouldBe(expectedVersion);
    }

    [Fact]
    public void Preset_bodies_no_longer_state_a_fixed_sentence_count()
    {
        foreach (var preset in new[]
                 {
                     SynthesisPresets.Zettelkasten, SynthesisPresets.Journal,
                     SynthesisPresets.Encyclopedic, SynthesisPresets.Technical,
                 })
        {
            SynthesisPresetBodies.BodyFor(preset).ShouldNotContain("Aim for");
        }
    }

    [Fact]
    public void Cache_key_is_stable_across_runs_and_independent_of_entity_state()
    {
        var rawHash = SynthesizingHandler.ComputeRawExtractionsHash(
            new Note { BodyInput = "hello world" },
            Array.Empty<Attachment>());

        var a = SynthesizingHandler.ComputeCacheKey(rawHash, "stub", "preset-zettelkasten-v3", "private", SynthesisPresets.Zettelkasten);
        var b = SynthesizingHandler.ComputeCacheKey(rawHash, "stub", "preset-zettelkasten-v3", "private", SynthesisPresets.Zettelkasten);
        a.ShouldBe(b);
    }
}
