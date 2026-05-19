using System.Text.Json;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm.Prompts;

public sealed class EntityExtractionDtoTests
{
    private static readonly string[] AcmeAliases = { "Acme Inc" };
    private static readonly string[] JohnnyAliases = { "Johnny" };

    [Fact]
    public void Deserializes_mentions_array()
    {
        const string json = """
            {"mentions":[
              {"anchor_text":"Acme","start_offset":10,"end_offset":14,
               "candidate_kind":"organization","candidate_canonical":"Acme Corp",
               "aliases":["Acme Inc"],"confidence":0.92}
            ]}
            """;
        var dto = JsonSerializer.Deserialize<EntityExtractionDto>(json)!;
        dto.Mentions.Count.ShouldBe(1);
        var m = dto.Mentions[0];
        m.AnchorText.ShouldBe("Acme");
        m.StartOffset.ShouldBe(10);
        m.EndOffset.ShouldBe(14);
        m.CandidateKind.ShouldBe("organization");
        m.CandidateCanonical.ShouldBe("Acme Corp");
        m.Aliases.ShouldBe(AcmeAliases);
        m.Confidence.ShouldBe(0.92);
    }

    [Fact]
    public void Deserializes_empty_mentions()
    {
        var dto = JsonSerializer.Deserialize<EntityExtractionDto>("""{"mentions":[]}""")!;
        dto.Mentions.Count.ShouldBe(0);
    }

    [Fact]
    public void Roundtrips_to_snake_case()
    {
        var dto = new EntityExtractionDto(new MentionCandidateDto[]
        {
            new("John", 0, 4, "person", "John Smith", JohnnyAliases, 0.8),
        });
        var json = JsonSerializer.Serialize(dto);
        json.ShouldContain("\"anchor_text\":\"John\"");
        json.ShouldContain("\"start_offset\":0");
        json.ShouldContain("\"end_offset\":4");
        json.ShouldContain("\"candidate_kind\":\"person\"");
        json.ShouldContain("\"candidate_canonical\":\"John Smith\"");
        json.ShouldContain("\"aliases\":[\"Johnny\"]");
    }
}
