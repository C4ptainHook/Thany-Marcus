using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class ImageRendererTests
{
    [Fact]
    public void Renders_with_presigned_embed_and_canonical_text()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Image,
            mime: "image/png",
            extractedText: "Description:\nA whiteboard sketch.\n\nText:\nqueued -> processing");
        var presigned = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [att.StorageKey] = "https://spaces.example.com/presigned",
        };
        var ctx = RendererTestSupport.NewContext(presigned: presigned);

        var md = new ImageRenderer().Render(att, ctx);

        md.ShouldStartWith("### Image\n");
        md.ShouldContain("![](https://spaces.example.com/presigned)");
        md.ShouldContain("Description:\nA whiteboard sketch.");
        md.ShouldContain("Text:\nqueued -> processing");
    }

    [Fact]
    public void Falls_back_to_bare_reference_when_presign_unavailable()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Image,
            mime: "image/png",
            extractedText: "Description:\nFoo");

        var md = new ImageRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldNotContain("![](");
        md.ShouldContain($"[image: {att.StorageKey}]");
    }
}
