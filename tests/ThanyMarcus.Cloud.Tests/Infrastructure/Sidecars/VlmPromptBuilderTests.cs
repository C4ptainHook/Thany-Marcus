using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class VlmPromptBuilderTests
{
    [Fact]
    public void Build_returns_the_locked_prompt()
    {
        var prompt = VlmPromptBuilder.Build();
        prompt.ShouldContain("Describe what is shown in this image");
        prompt.ShouldContain("Do not output JSON");
        prompt.ShouldContain("Do not mention EXIF");
    }

    [Fact]
    public void Build_is_deterministic()
    {
        VlmPromptBuilder.Build().ShouldBe(VlmPromptBuilder.Build());
    }
}
