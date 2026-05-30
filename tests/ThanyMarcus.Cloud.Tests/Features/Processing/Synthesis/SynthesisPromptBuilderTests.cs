using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class SynthesisPromptBuilderTests
{
    private static readonly SynthesisInput[] NoInputs = Array.Empty<SynthesisInput>();

    [Fact]
    public void Build_does_not_emit_an_available_entities_section()
    {
        var prompt = SynthesisPromptBuilder.Build(
            systemBody: SynthesisPresetBodies.BodyFor(SynthesisPresets.Zettelkasten),
            userBody: "Met Mike at the Slack offsite.",
            attachmentInputs: NoInputs);

        prompt.ShouldNotContain("Available entities");
        prompt.ShouldNotContain("use ONLY these");
        prompt.ShouldContain("Met Mike at the Slack offsite.");
        prompt.ShouldContain("Write the synthesized note now.");
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
    [InlineData(SynthesisPresets.Zettelkasten, "preset-zettelkasten-v2")]
    [InlineData(SynthesisPresets.Journal, "preset-journal-v2")]
    [InlineData(SynthesisPresets.Encyclopedic, "preset-encyclopedic-v2")]
    [InlineData(SynthesisPresets.Technical, "preset-technical-v2")]
    public void Preset_versions_are_bumped_to_v2(string preset, string expectedVersion)
    {
        SynthesisPresetBodies.VersionFor(preset).ShouldBe(expectedVersion);
    }

    [Fact]
    public void Cache_key_is_stable_across_runs_and_independent_of_entity_state()
    {
        var rawHash = SynthesizingHandler.ComputeRawExtractionsHash(
            new ThanyMarcus.Cloud.Api.Features.Ingest.Note { BodyInput = "hello world" },
            Array.Empty<ThanyMarcus.Cloud.Api.Features.Ingest.Attachment>());

        var a = SynthesizingHandler.ComputeCacheKey(rawHash, "stub", "preset-zettelkasten-v2", "private", SynthesisPresets.Zettelkasten);
        var b = SynthesizingHandler.ComputeCacheKey(rawHash, "stub", "preset-zettelkasten-v2", "private", SynthesisPresets.Zettelkasten);
        a.ShouldBe(b);
    }
}
