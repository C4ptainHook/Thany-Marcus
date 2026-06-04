using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class EssenceLayoutTests
{
    private const string Frontmatter = "thany_note_id: abc\nthany_updated_at: 2026-06-03T12:00:00Z\nthany_locked: true\n";
    private const string Essence = "# Title\n#focus\n\nA distilled [[idea]].";
    private const string Origin = "my raw verbatim capture";
    private const string Sources = "## Sources\n\n> [!source]- Voice — ![[memo.wav]]\n> transcript";
    private const string Processing = "> [!info]- Processing details\n> Model: qwen · private · ok";

    private static string Assemble() =>
        EssenceLayout.Assemble(Frontmatter, Essence, Origin, Sources, Processing);

    [Fact]
    public void Wraps_each_machine_owned_region_in_managed_fences()
    {
        var body = Assemble();
        body.ShouldContain(EssenceLayout.EssenceOpen);
        body.ShouldContain(EssenceLayout.EssenceClose);
        body.ShouldContain(EssenceLayout.SourcesOpen);
        body.ShouldContain(EssenceLayout.SourcesClose);
        body.ShouldContain(EssenceLayout.ProcessingOpen);
        body.ShouldContain(EssenceLayout.ProcessingClose);
    }

    [Fact]
    public void Frontmatter_is_the_literal_top_of_the_file()
    {
        Assemble().ShouldStartWith("---\nthany_note_id: abc");
    }

    [Fact]
    public void Origin_holds_the_verbatim_capture_outside_the_essence_fence()
    {
        var body = Assemble();
        body.ShouldContain("## Origin\n\nmy raw verbatim capture");
        body.IndexOf(Origin, StringComparison.Ordinal)
            .ShouldBeGreaterThan(body.IndexOf(EssenceLayout.EssenceClose, StringComparison.Ordinal));
    }

    [Fact]
    public void Processing_details_come_after_the_sources_block()
    {
        var body = Assemble();
        body.IndexOf(EssenceLayout.ProcessingOpen, StringComparison.Ordinal)
            .ShouldBeGreaterThan(body.IndexOf(EssenceLayout.SourcesClose, StringComparison.Ordinal));
    }

    [Fact]
    public void Essence_renders_before_origin()
    {
        var body = Assemble();
        body.IndexOf(EssenceLayout.EssenceOpen, StringComparison.Ordinal)
            .ShouldBeLessThan(body.IndexOf("## Origin", StringComparison.Ordinal));
    }
}
