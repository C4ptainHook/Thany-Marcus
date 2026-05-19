using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

public sealed class AudioRendererTests
{
    [Fact]
    public void Renders_voice_memo_heading_with_filename()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Voice,
            mime: "audio/wav",
            filename: "2026-05-19-meeting.m4a",
            extractedText: "Plain transcription text.");

        var md = new AudioRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldContain("### Voice memo: 2026-05-19-meeting.m4a");
        md.ShouldContain("[audio: 2026-05-19-meeting.m4a]");
        md.ShouldContain("Plain transcription text.");
    }

    [Fact]
    public void Renders_without_filename()
    {
        var att = RendererTestSupport.NewAttachment(
            AttachmentKind.Voice,
            mime: "audio/wav",
            extractedText: "transcription");

        var md = new AudioRenderer().Render(att, RendererTestSupport.NewContext());

        md.ShouldStartWith("### Voice memo\n");
        md.ShouldContain($"[audio: {att.StorageKey}]");
    }
}
