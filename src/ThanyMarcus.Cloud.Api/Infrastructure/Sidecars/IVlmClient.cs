using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IVlmClient
{
    Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct);
}

public sealed record VlmExtractionOutcome(
    string? ExtractedText,
    string? ExtractionCacheKey,
    JsonDocument Extra,
    bool Skipped,
    string? SkipReason);
