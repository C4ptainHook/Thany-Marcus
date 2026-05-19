using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class VideoRendererTests
{
    [Fact]
    public void Matches_only_video_mimes_under_file_kind()
    {
        var renderer = new VideoRenderer();
        renderer.Matches(RendererTestSupport.NewAttachment(AttachmentKind.File, mime: "video/mp4")).ShouldBeTrue();
        renderer.Matches(RendererTestSupport.NewAttachment(AttachmentKind.File, mime: "video/quicktime")).ShouldBeTrue();
        renderer.Matches(RendererTestSupport.NewAttachment(AttachmentKind.File, mime: "application/pdf")).ShouldBeFalse();
        renderer.Matches(RendererTestSupport.NewAttachment(AttachmentKind.Image, mime: "image/png")).ShouldBeFalse();
    }

    [Fact]
    public void Renders_audio_then_keyframes_in_created_order()
    {
        var parent = RendererTestSupport.NewAttachment(
            AttachmentKind.File, mime: "video/mp4", filename: "lecture.mp4");

        var t0 = Instant.FromUtc(2026, 5, 19, 10, 0);
        var audioChild = RendererTestSupport.NewAttachment(
            AttachmentKind.Voice, mime: "audio/wav",
            extractedText: "transcribed audio",
            parentAttachmentId: parent.Id,
            createdAt: t0);
        var kf2 = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractedText: "Description:\nslide two\n\nText:\nbody",
            parentAttachmentId: parent.Id,
            createdAt: t0.Plus(Duration.FromSeconds(2)));
        var kf1 = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractedText: "Description:\nslide one\n\nText:\ntitle",
            parentAttachmentId: parent.Id,
            createdAt: t0.Plus(Duration.FromSeconds(1)));

        var childrenMap = new Dictionary<Guid, IReadOnlyList<Attachment>>
        {
            [parent.Id] = new[] { audioChild, kf2, kf1 },
        };
        var ctx = RendererTestSupport.NewContext(
            children: childrenMap,
            renderChild: (child, _) => "RENDERED:" + child.ExtractedText);

        var md = new VideoRenderer().Render(parent, ctx);

        md.ShouldStartWith("### Video: lecture.mp4\n");
        md.ShouldContain("[video: lecture.mp4]");
        md.ShouldContain("**Audio:**\nRENDERED:transcribed audio");
        md.ShouldContain("**Keyframes:**");
        var keyframesSection = md.Substring(md.IndexOf("**Keyframes:**", StringComparison.Ordinal));
        var slideOnePos = keyframesSection.IndexOf("slide one", StringComparison.Ordinal);
        var slideTwoPos = keyframesSection.IndexOf("slide two", StringComparison.Ordinal);
        slideOnePos.ShouldBeGreaterThan(0);
        slideTwoPos.ShouldBeGreaterThan(slideOnePos);
    }

    [Fact]
    public void Renders_without_audio_or_keyframes_when_no_children()
    {
        var parent = RendererTestSupport.NewAttachment(
            AttachmentKind.File, mime: "video/mp4", filename: "clip.mp4");

        var md = new VideoRenderer().Render(parent, RendererTestSupport.NewContext());

        md.ShouldContain("### Video: clip.mp4");
        md.ShouldContain("[video: clip.mp4]");
        md.ShouldNotContain("**Audio:**");
        md.ShouldNotContain("**Keyframes:**");
    }
}
