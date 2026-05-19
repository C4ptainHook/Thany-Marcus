using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class FailedHiddenRendererTests
{
    [Fact]
    public void Renders_failed_attachment_as_single_line_html_comment()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Image,
            extractionStatus: AttachmentExtractionStatus.Failed,
            extractionError: "ollama_json_parse_after_3_attempts");

        var md = new FailedHiddenRenderer().Render(att);

        md.ShouldStartWith("<!-- thany-marcus:attachment ");
        md.ShouldContain($"id={att.Id}");
        md.ShouldContain("kind=image");
        md.ShouldContain("status=failed");
        md.ShouldContain("reason=ollama_json_parse_after_3_attempts");
        md.TrimEnd('\n').ShouldNotContain('\n');
    }

    [Fact]
    public void Skipped_with_null_text_renders_as_skipped_marker()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Image,
            extractionStatus: AttachmentExtractionStatus.Skipped,
            extractedText: null,
            extractionError: "preflight_dimensions_out_of_range:50x50");

        var md = new FailedHiddenRenderer().Render(att);

        md.ShouldContain("status=skipped");
        md.ShouldContain("reason=preflight_dimensions_out_of_range:50x50");
    }

    [Fact]
    public void Strips_newlines_and_dashes_from_reason()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Voice,
            extractionStatus: AttachmentExtractionStatus.Failed,
            extractionError: "line1\r\nline2 -- whatever");

        var md = new FailedHiddenRenderer().Render(att);

        md.ShouldContain("reason=line1 line2");
        md.ShouldNotContain('\r');
        md.TrimEnd('\n').ShouldNotContain('\n');
    }

    [Fact]
    public void Missing_error_defaults_to_unknown()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Image,
            extractionStatus: AttachmentExtractionStatus.Failed);

        var md = new FailedHiddenRenderer().Render(att);

        md.ShouldContain("reason=unknown");
    }
}
