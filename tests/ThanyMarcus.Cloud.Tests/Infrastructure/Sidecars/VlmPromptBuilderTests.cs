using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class VlmPromptBuilderTests
{
    [Fact]
    public void Build_returns_the_locked_prompt()
    {
        var prompt = VlmPromptBuilder.Build();
        prompt.ShouldContain("\"description\"");
        prompt.ShouldContain("\"text_in_image\"");
        prompt.ShouldContain("Output ONLY the JSON object.");
        prompt.ShouldContain("If no text is present, return null.");
    }

    [Fact]
    public void Build_is_deterministic()
    {
        VlmPromptBuilder.Build().ShouldBe(VlmPromptBuilder.Build());
    }
}
