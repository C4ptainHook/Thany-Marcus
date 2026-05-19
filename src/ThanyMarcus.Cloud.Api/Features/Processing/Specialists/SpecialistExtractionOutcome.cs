using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed record SpecialistExtractionOutcome(
    string? ExtractedText,
    string? ExtractionCacheKey,
    JsonDocument? Extra,
    string AttachmentStatus,
    string? ExtractionError);
