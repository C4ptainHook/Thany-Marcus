using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

public sealed class ExtractingAttachmentsRoutingTests
{
    private static Attachment Att(string mode, string kind) =>
        new() { Mode = mode, Kind = kind };

    [Fact]
    public void Reference_mode_routes_to_no_sidecar()
    {
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Reference, AttachmentKind.Voice)).ShouldBeNull();
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Reference, AttachmentKind.File)).ShouldBeNull();
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Reference, AttachmentKind.Url)).ShouldBeNull();
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Reference, AttachmentKind.Image)).ShouldBeNull();
    }

    [Fact]
    public void Metadata_url_routes_to_url_metadata_sidecar()
    {
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Metadata, AttachmentKind.Url))
            .ShouldBe(ExtractionTaskSidecar.UrlMetadata);
    }

    [Fact]
    public void Extract_mode_preserves_today_routing()
    {
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Extract, AttachmentKind.Image))
            .ShouldBe(ExtractionTaskSidecar.Ollama);
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Extract, AttachmentKind.Voice))
            .ShouldBe(ExtractionTaskSidecar.Parakeet);
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Extract, AttachmentKind.File))
            .ShouldBe(ExtractionTaskSidecar.Docling);
        ExtractingAttachmentsHandler.ResolveSidecar(Att(AttachmentMode.Extract, AttachmentKind.Url))
            .ShouldBe(ExtractionTaskSidecar.Url);
    }
}
