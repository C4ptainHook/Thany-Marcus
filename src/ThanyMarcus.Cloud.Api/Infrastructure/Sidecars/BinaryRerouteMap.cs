using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public static class BinaryRerouteMap
{
    public static BinaryRerouteTarget Resolve(string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        var mime = mimeType.Trim().ToLowerInvariant();
        var primary = mime.Split('/', 2)[0];

        return primary switch
        {
            "image" => new BinaryRerouteTarget(AttachmentKind.Image, ExtractionTaskSidecar.Ollama, mime),
            "audio" => new BinaryRerouteTarget(AttachmentKind.Voice, ExtractionTaskSidecar.Parakeet, mime),
            // FORK: video specialist worker is stub until handoff #6; gated by IngestSaga:Specialists:Video:Enabled.
            "video" => new BinaryRerouteTarget(AttachmentKind.File, ExtractionTaskSidecar.Video, mime),
            _ => mime switch
            {
                "application/pdf"
                    or "application/zip" // FORK: docling decides if it can parse; if not, attachment fails with the error.
                    or "application/msword"
                    or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                    or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
                    or "application/vnd.ms-excel"
                    or "application/vnd.ms-powerpoint"
                    or "application/vnd.oasis.opendocument.text"
                    or "application/vnd.oasis.opendocument.spreadsheet"
                    or "application/vnd.oasis.opendocument.presentation"
                    => new BinaryRerouteTarget(AttachmentKind.File, ExtractionTaskSidecar.Docling, mime),
                _ => throw new UnsupportedRerouteException(mime),
            },
        };
    }
}

public sealed record BinaryRerouteTarget(string AttachmentKind, string TargetSidecar, string MimeType);

public sealed class UnsupportedRerouteException(string mimeType)
    : Exception($"Unsupported reroute mime type: {mimeType}")
{
    public string MimeType { get; } = mimeType;
}
