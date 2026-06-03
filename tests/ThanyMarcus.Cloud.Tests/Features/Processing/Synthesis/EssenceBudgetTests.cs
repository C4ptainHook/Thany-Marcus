using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class EssenceBudgetTests
{
    private static readonly int[] Ladder = { 0, 24, 25, 79, 80, 249, 250, 599, 600, 10_000 };

    [Theory]
    [InlineData(10, 2)]
    [InlineData(50, 3)]
    [InlineData(150, 5)]
    [InlineData(400, 7)]
    [InlineData(2000, 8)]
    public void For_maps_input_tokens_to_a_proportional_unit_band(int tokens, int expectedUnits)
    {
        EssenceBudget.For(tokens).Units.ShouldBe(expectedUnits);
    }

    [Fact]
    public void For_is_monotonic_non_decreasing_and_clamped()
    {
        var ladder = Ladder
            .Select(t => EssenceBudget.For(t).Units)
            .ToList();

        for (var i = 1; i < ladder.Count; i++)
        {
            ladder[i].ShouldBeGreaterThanOrEqualTo(ladder[i - 1]);
        }
        ladder.First().ShouldBe(2);
        ladder.Last().ShouldBe(8);
    }

    [Fact]
    public void EstimateTokens_sums_body_and_extraction_chars_over_four()
    {
        var inputs = new[]
        {
            new SynthesisInput("voice", Content: new string('x', 8), FailureReason: null),
            new SynthesisInput("url", Content: null, FailureReason: null,
                Mode: AttachmentMode.Metadata, Title: "abcd", Description: "ef"),
            new SynthesisInput("image", Content: null, FailureReason: "boom"),
        };

        EssenceBudget.EstimateTokens(new string('y', 4), inputs).ShouldBe((4 + 8 + 6) / 4);
    }
}
