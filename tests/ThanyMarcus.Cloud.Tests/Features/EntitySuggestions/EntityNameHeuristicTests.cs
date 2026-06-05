using Shouldly;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

namespace ThanyMarcus.Cloud.Tests.Features.EntitySuggestions;

public sealed class EntityNameHeuristicTests
{
    [Theory]
    [InlineData("Київ")]
    [InlineData("OpenAI")]
    [InlineData("Sarah Chen")]
    [InlineData("S. Chen")]
    [InlineData("Project Atlas")]
    [InlineData("Berlin")]
    [InlineData(".NET")]
    public void Accepts_proper_noun_names(string value)
    {
        EntityNameHeuristic.LooksLikeName(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("zero stock picking")]
    [InlineData("automatic monthly contributions")]
    [InlineData("low-cost broad-market index funds")]
    public void Rejects_lowercase_common_noun_phrases(string value)
    {
        EntityNameHeuristic.LooksLikeName(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("She wants the OpenAI integration shipped before the Berlin offsite.")]
    [InlineData("The Quick Brown Fox Jumps")]
    [InlineData("Berlin.")]
    [InlineData("Wait!")]
    [InlineData("Note:")]
    [InlineData("Really?")]
    public void Rejects_sentences_and_over_length_spans(string value)
    {
        EntityNameHeuristic.LooksLikeName(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty(string? value)
    {
        EntityNameHeuristic.LooksLikeName(value).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_names_over_forty_characters()
    {
        EntityNameHeuristic.LooksLikeName(new string('A', 41)).ShouldBeFalse();
    }
}
