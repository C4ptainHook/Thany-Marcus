using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class ProcessingDetailsRendererTests
{
    private static SynthesisFrontmatterFields Fields(string status = "ok", string? error = null) =>
        new(
            PrivacyMode: "private",
            Model: "qwen3:1.7b-q4_K_M",
            Preset: "zettelkasten",
            PromptVersion: "preset-zettelkasten-v3",
            Seed: 42,
            SynthesizedAt: Instant.FromUtc(2026, 6, 3, 12, 30),
            Status: status,
            Error: error);

    [Fact]
    public void Renders_a_collapsed_info_callout_with_provenance()
    {
        var md = ProcessingDetailsRenderer.Render(Fields());

        md.ShouldContain("> [!info]- Processing details");
        md.ShouldContain("qwen3:1.7b-q4_K_M");
        md.ShouldContain("private");
        md.ShouldContain("preset zettelkasten v3");
        md.ShouldContain("seed 42");
        md.ShouldContain("synthesized 2026-06-03");
        md.ShouldContain("· ok");
    }

    [Fact]
    public void Failure_adds_an_error_line()
    {
        var md = ProcessingDetailsRenderer.Render(Fields(status: "failed", error: "Ollama returned 500"));
        md.ShouldContain("· failed");
        md.ShouldContain("> error: Ollama returned 500");
    }

    [Fact]
    public void Custom_preset_falls_back_to_the_full_prompt_version()
    {
        var fields = Fields() with { Preset = "custom", PromptVersion = "custom-ab12cd34" };
        ProcessingDetailsRenderer.Render(fields).ShouldContain("preset custom custom-ab12cd34");
    }
}
