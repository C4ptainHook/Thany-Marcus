using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public sealed record SynthesisInput(
    string Kind,           // "voice" | "image" | "url" | "file"
    string? Content,       // null if extraction failed or non-extract mode
    string? FailureReason,
    string Mode = AttachmentMode.Extract,
    string? Id = null,
    string? Title = null,
    string? Description = null,
    string? Url = null);

public sealed record ExtractionFailureInfo(
    string Kind,
    Guid AttachmentId,
    string Reason);
