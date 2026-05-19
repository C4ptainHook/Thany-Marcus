using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class UrlRendererTests
{
    [Fact]
    public void Renders_with_title_from_extra()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "article body",
            extraJson: "{\"title\":\"How frontmatter works\",\"url\":\"https://example.org/x\"}");
        var ctx = RendererTestSupport.NewContext();

        var md = new UrlRenderer().Render(att, ctx);

        md.ShouldContain("### Source: How frontmatter works");
        md.ShouldContain("Source: https://example.org/x");
        md.ShouldContain("article body");
    }

    [Fact]
    public void Falls_back_to_canonical_url_when_title_missing()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "x",
            extraJson: "{\"canonical_url\":\"https://example.org/canon\",\"url\":\"https://example.org/raw\"}");
        var ctx = RendererTestSupport.NewContext();

        var md = new UrlRenderer().Render(att, ctx);

        md.ShouldContain("### Source: https://example.org/canon");
        md.ShouldContain("Source: https://example.org/canon");
    }

    [Fact]
    public void Falls_back_to_attachment_url_when_no_title_or_canonical()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "x",
            extraJson: "{\"url\":\"https://example.org/raw\"}");
        var ctx = RendererTestSupport.NewContext();

        var md = new UrlRenderer().Render(att, ctx);

        md.ShouldContain("### Source: https://example.org/raw");
        md.ShouldContain("Source: https://example.org/raw");
    }

    [Fact]
    public void Truncates_long_heading_to_80_chars_with_ellipsis()
    {
        var longTitle = new string('A', 200);
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "x",
            extraJson: $"{{\"title\":\"{longTitle}\",\"url\":\"https://example.org/x\"}}");
        var ctx = RendererTestSupport.NewContext();

        var md = new UrlRenderer().Render(att, ctx);

        var headingLine = md.Split('\n')[0];
        headingLine.Length.ShouldBe("### Source: ".Length + 80);
        headingLine.ShouldEndWith("…");
    }

    [Fact]
    public void Empty_extracted_text_emits_no_content_marker()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: null,
            extraJson: "{\"url\":\"https://example.org\"}");

        var md = new UrlRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldContain("<!-- no extracted content -->");
    }
}
