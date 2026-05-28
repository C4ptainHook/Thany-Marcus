namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public sealed record SynthesisInput(
    string Kind,           // "voice" | "image" | "url" | "file"
    string? Content,       // null if extraction failed
    string? FailureReason);

public sealed record ExtractionFailureInfo(
    string Kind,
    Guid AttachmentId,
    string Reason);
