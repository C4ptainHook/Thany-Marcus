using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Prompts;

public sealed class PromptBuilderTests
{
    private static readonly string[] AcmeIncAliases = { "Acme Inc" };
    private static readonly string[] AcmeAliases = { "Acme" };
    private static readonly string[] JohnnyAliases = { "Johnny" };

    [Fact]
    public void BuildRoute_with_folders_emits_folder_names()
    {
        var folders = new[] { "Acme", "Beta" };
        var prompt = PromptBuilder.BuildRoute(folders, "the body");
        prompt.ShouldContain("Acme");
        prompt.ShouldContain("Beta");
        prompt.ShouldContain("the body");
        prompt.ShouldContain("Respond with JSON ONLY");
        prompt.ShouldNotContain("9b3c4f12");
    }

    [Fact]
    public void BuildRoute_with_no_folders_emits_none_marker()
    {
        var prompt = PromptBuilder.BuildRoute(Array.Empty<string>(), "body");
        prompt.ShouldContain("(none)");
    }

    [Fact]
    public void BuildExtract_substitutes_body()
    {
        var prompt = PromptBuilder.BuildExtract("Hello Acme world.");
        prompt.ShouldContain("Hello Acme world.");
        prompt.ShouldContain("\"mentions\":");
    }

    [Fact]
    public void BuildDedup_lists_neighbors_with_aliases()
    {
        var cand = new MentionCandidateDto(
            AnchorText: "Acme",
            StartOffset: 0,
            EndOffset: 4,
            CandidateKind: "organization",
            CandidateCanonical: "Acme Corp",
            Aliases: AcmeIncAliases,
            Confidence: 0.9);
        var neighbors = new[]
        {
            new EntityNeighbor(
                Guid.Parse("9b3c4f12-0000-0000-0000-000000000001"),
                "organization",
                "Acme Corporation",
                AcmeAliases,
                "a supplier"),
        };
        var prompt = PromptBuilder.BuildDedup(cand, "surrounding text here", neighbors);
        prompt.ShouldContain("Acme Corp");
        prompt.ShouldContain("surrounding text here");
        prompt.ShouldContain("Acme Corporation");
        prompt.ShouldContain("a supplier");
        prompt.ShouldContain("\"decision\"");
    }

    [Fact]
    public void BuildDedup_with_no_neighbors_emits_none_marker()
    {
        var cand = new MentionCandidateDto("X", 0, 1, "person", "Xavier",
            Array.Empty<string>(), 0.7);
        var prompt = PromptBuilder.BuildDedup(cand, "ctx", Array.Empty<EntityNeighbor>());
        prompt.ShouldContain("(none)");
    }

    [Fact]
    public void BuildHubGenerate_first_pass_uses_from_scratch_instruction()
    {
        var entity = new HubEntityContext("person", "John Smith", JohnnyAliases);
        var mentions = new[]
        {
            new HubMentionContext("Inbox/abc.md", "2026-05-19T10:00:00Z", "John ran the meeting"),
        };
        var prompt = PromptBuilder.BuildHubGenerate(entity, mentions, previousBody: null);
        prompt.ShouldContain("John Smith");
        prompt.ShouldContain("Johnny");
        prompt.ShouldContain("Generate the dossier from scratch.");
        prompt.ShouldNotContain("PREVIOUS DOSSIER");
        prompt.ShouldContain("John ran the meeting");
    }

    [Fact]
    public void BuildHubGenerate_diff_aware_includes_previous_body()
    {
        var entity = new HubEntityContext("person", "John Smith", Array.Empty<string>());
        var prompt = PromptBuilder.BuildHubGenerate(
            entity,
            Array.Empty<HubMentionContext>(),
            previousBody: "### Context\n- known fact");
        prompt.ShouldContain("PREVIOUS DOSSIER");
        prompt.ShouldContain("known fact");
        prompt.ShouldContain("(no mentions available)");
    }
}
