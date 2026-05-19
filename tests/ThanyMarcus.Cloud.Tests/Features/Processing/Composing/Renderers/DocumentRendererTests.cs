using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class DocumentRendererTests
{
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("application/msword")]
    public void Matches_known_document_mimes(string mime)
    {
        var att = RendererTestSupport.NewAttachment(AttachmentKind.File, mime: mime);
        new DocumentRenderer().Matches(att).ShouldBeTrue();
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData(null)]
    [InlineData("application/octet-stream")]
    public void Does_not_match_non_document_mimes(string? mime)
    {
        var att = RendererTestSupport.NewAttachment(AttachmentKind.File, mime: mime);
        new DocumentRenderer().Matches(att).ShouldBeFalse();
    }

    [Fact]
    public void Renders_document_with_filename_and_extracted_markdown()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.File,
            mime: "application/pdf",
            filename: "spec.pdf",
            extractedText: "# Heading\n\nBody.");

        var md = new DocumentRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldContain("### Document: spec.pdf");
        md.ShouldContain("[document: spec.pdf]");
        md.ShouldContain("# Heading\n\nBody.");
    }

    [Fact]
    public void Renders_untitled_when_filename_missing()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.File,
            mime: "application/pdf",
            extractedText: "body");

        var md = new DocumentRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldContain("### Document: untitled");
        md.ShouldContain("[document: untitled]");
    }
}
