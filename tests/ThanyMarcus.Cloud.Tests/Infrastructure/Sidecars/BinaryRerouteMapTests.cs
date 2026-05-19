using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class BinaryRerouteMapTests
{
    [Theory]
    [InlineData("image/jpeg", AttachmentKind.Image, ExtractionTaskSidecar.Ollama)]
    [InlineData("image/png", AttachmentKind.Image, ExtractionTaskSidecar.Ollama)]
    [InlineData("audio/mpeg", AttachmentKind.Voice, ExtractionTaskSidecar.Parakeet)]
    [InlineData("video/mp4", AttachmentKind.File, ExtractionTaskSidecar.Video)]
    [InlineData("application/pdf", AttachmentKind.File, ExtractionTaskSidecar.Docling)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        AttachmentKind.File, ExtractionTaskSidecar.Docling)]
    public void Maps_mime_to_kind_and_sidecar(string mime, string expectedKind, string expectedSidecar)
    {
        var target = BinaryRerouteMap.Resolve(mime);
        target.AttachmentKind.ShouldBe(expectedKind);
        target.TargetSidecar.ShouldBe(expectedSidecar);
        target.MimeType.ShouldBe(mime);
    }

    [Fact]
    public void Unknown_mime_throws()
    {
        Should.Throw<UnsupportedRerouteException>(() => BinaryRerouteMap.Resolve("application/octet-stream"));
    }
}
